using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.DataModel;
using NexusMods.DataModel.LegacyData;
using NexusMods.DataModel.Storage;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;
using R3;

namespace NexusMods.App.UI.Overlays;

/// <summary>
/// Guides the user from the old <c>.nx</c> storage to a clean start: rescue old downloads, clean the game folder,
/// verify in Steam, then reset the database and restart.
/// </summary>
public class LegacyCleanupOverlayViewModel : AOverlayViewModel<ILegacyCleanupOverlayViewModel>, ILegacyCleanupOverlayViewModel
{
    private const string SteamValidateUri = "steam://validate/1091500";

    public BindableReactiveProperty<int> Step { get; } = new(1);
    public BindableReactiveProperty<string> LegacyDownloadsText { get; } = new("");
    public BindableReactiveProperty<bool> DeleteProtonPrefix { get; } = new(false);
    public BindableReactiveProperty<bool> IsBusy { get; } = new(false);
    public BindableReactiveProperty<string> Message { get; } = new("");
    public BindableReactiveProperty<bool> CanSkipCleanup { get; } = new(false);
    public BindableReactiveProperty<bool> CleanupSkipped { get; } = new(false);
    public ReactiveCommand<Unit> CommandNext { get; }
    public ReactiveCommand<Unit> CommandSkipCleanup { get; }
    public ReactiveCommand<Unit> CommandQuit { get; }
    public ReactiveCommand<Unit> CommandVerifySteam { get; }

    public LegacyCleanupOverlayViewModel(
        IStorageAnalyzer storage,
        IOSInterop osInterop,
        ILogger logger,
        Func<AbsolutePath?> steamLibraryRoot,
        Action resetAndRestart,
        Action quit)
    {
        // Set once the Deep Clean ran, so a step-3 retry (e.g. after the Proton prefix failed) doesn't run it again.
        var deepCleanDone = false;

        CommandQuit = IsBusy.Select(static busy => !busy).ToReactiveCommand<Unit>(_ => quit());
        CommandVerifySteam = new ReactiveCommand<Unit>(_ =>
        {
            try
            {
                osInterop.OpenUri(new Uri(SteamValidateUri));
            }
            catch (Exception e)
            {
                logger.LogError(e, "No se pudo abrir Steam para verificar los archivos");
                Message.Value = $"No se pudo abrir Steam: {e.Message}. Verificá los archivos desde Steam: Propiedades > Archivos instalados.";
            }
        });

        // After a failed step 3 the user can always move on to the reset, even if the game folder can't be cleaned.
        CommandSkipCleanup = IsBusy.Select(static busy => !busy).ToReactiveCommand<Unit>(_ =>
        {
            CleanupSkipped.Value = !deepCleanDone;
            CanSkipCleanup.Value = false;
            Message.Value = "";
            Step.Value = 4;
        });

        CommandNext = IsBusy.Select(static busy => !busy).ToReactiveCommand<Unit>(
            executeAsync: async (_, cancellationToken) =>
            {
                IsBusy.Value = true;
                Message.Value = "";
                var restarting = false;
                try
                {
                    switch (Step.Value)
                    {
                        case 1:
                            var (count, size) = await storage.GetLegacyDownloadsAsync(cancellationToken);
                            LegacyDownloadsText.Value = count == 0
                                ? "No hay descargas viejas para rescatar."
                                : $"{count} archivos ({ByteSize.FromBytes(size.Value).Humanize("0.#")}) en NexusMods.App/Downloads. Se mueven a tModManager/Downloads.";
                            break;
                        case 2:
                            await storage.MoveLegacyDownloadsAsync(cancellationToken);
                            break;
                        case 3:
                            if (!deepCleanDone)
                            {
                                // Without the apply/ingest syncs: those need the old .nx archives.
                                await storage.RunDeepCleanWithoutSyncOnAllLoadoutsAsync(cancellationToken);
                                deepCleanDone = true;
                            }

                            if (DeleteProtonPrefix.Value)
                            {
                                if (steamLibraryRoot() is { } root)
                                    await storage.DeleteProtonPrefixAsync(root, cancellationToken);
                                else
                                    Message.Value = "No se encontró la librería de Steam del juego; el prefix de Proton no se borró.";
                            }
                            break;
                        default:
                            resetAndRestart();
                            // Keep the buttons disabled while the app shuts down.
                            restarting = true;
                            return;
                    }

                    Step.Value++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Stay on the same step so the user can retry; a failed step never lets the wizard advance.
                    logger.LogError(e, "Falló el paso {Step} de la limpieza de datos viejos", Step.Value);
                    Message.Value = $"Algo salió mal: {e.Message}. Podés reintentar este paso.";
                    if (Step.Value == 3) CanSkipCleanup.Value = true;
                }
                finally
                {
                    if (!restarting) IsBusy.Value = false;
                }
            },
            awaitOperation: AwaitOperation.Drop
        );
    }

    public static ILegacyCleanupOverlayViewModel? CreateIfNeeded(IServiceProvider serviceProvider)
    {
        var fs = serviceProvider.GetRequiredService<IFileSystem>();
        var archives = serviceProvider.GetRequiredService<ISettingsManager>().Get<DataModelSettings>().ArchiveLocations[0].ToPath(fs);
        if (!LegacyDataDetector.HasNxArchives(archives)) return null;

        var osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        return new LegacyCleanupOverlayViewModel(
            storage: serviceProvider.GetRequiredService<IStorageAnalyzer>(),
            osInterop: osInterop,
            logger: serviceProvider.GetRequiredService<ILogger<LegacyCleanupOverlayViewModel>>(),
            steamLibraryRoot: () => SteamLibraryRoot(serviceProvider),
            resetAndRestart: () =>
            {
                LegacyDataDetector.RequestResetOnStart(fs);
                AppRestart.RestartAfterExit(osInterop);
            },
            quit: AppRestart.Shutdown
        );
    }

    /// <summary>The Steam library that holds the first managed game (the folder containing <c>steamapps</c>), or null.</summary>
    private static AbsolutePath? SteamLibraryRoot(IServiceProvider serviceProvider)
    {
        var db = serviceProvider.GetRequiredService<IConnection>().Db;
        foreach (var loadout in Loadout.All(db))
        {
            if (!loadout.IsVisible()) continue;
            return FindSteamLibraryRoot(loadout.InstallationInstance.Locations[LocationId.Game].Path);
        }

        return null;
    }

    /// <summary>Walks up from <paramref name="gamePath"/> (<c>&lt;lib&gt;/steamapps/common/&lt;game&gt;</c>) to the directory containing <c>steamapps</c>.</summary>
    internal static AbsolutePath? FindSteamLibraryRoot(AbsolutePath gamePath)
    {
        for (var dir = gamePath; ; dir = dir.Parent)
        {
            if (dir.Combine("steamapps").DirectoryExists()) return dir;
            if (dir == dir.Parent) return null;
        }
    }
}
