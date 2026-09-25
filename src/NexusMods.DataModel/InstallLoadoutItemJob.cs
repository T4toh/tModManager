using System.Diagnostics;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Games.AdvancedInstaller;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Library;

namespace NexusMods.DataModel;

internal class InstallLoadoutItemJob : IJobDefinitionWithStart<InstallLoadoutItemJob, InstallLoadoutItemJobResult>, IInstallLoadoutItemJob
{
    public required ILogger Logger { get; init; }
    public ILibraryItemInstaller? Installer { get; init; }
    public ILibraryItemInstaller? FallbackInstaller { get; init; }
    public LibraryItem.ReadOnly LibraryItem { get; init; }
    public Optional<LoadoutItemGroupId> ParentGroupId { get; set; }
    public LoadoutId LoadoutId { get; init; }
    
    public required ITransaction Transaction { get; init; }
    internal required IConnection Connection { get; init; }
    internal required IServiceProvider ServiceProvider { get; init; }

    /// <remarks>
    /// Returns null <see cref="LoadoutItemGroup.ReadOnly"/> after running job
    /// if supplied an external transaction via <paramref name="transaction"/>.
    ///
    /// (i.e. if you are running this job as part of a larger transaction)
    /// </remarks>
    public static InstallLoadoutItemJob Create(
        IServiceProvider serviceProvider,
        LibraryItem.ReadOnly libraryItem,
        LoadoutId targetLoadout,
        ITransaction transaction,
        Optional<LoadoutItemGroupId> groupId = default,
        ILibraryItemInstaller? installer = null,
        ILibraryItemInstaller? fallbackInstaller = null)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var job = new InstallLoadoutItemJob
        {
            Logger = serviceProvider.GetRequiredService<ILogger<InstallLoadoutItemJob>>(),
            Installer = installer,
            FallbackInstaller = fallbackInstaller,
            LibraryItem = libraryItem,
            LoadoutId = targetLoadout,
            ParentGroupId = groupId,
            Connection = connection,
            ServiceProvider = serviceProvider,
            Transaction = transaction,
        };

        return job;
    }

    public async ValueTask<InstallLoadoutItemJobResult> StartAsync(IJobContext<InstallLoadoutItemJob> context)
    {
        if (!ParentGroupId.HasValue)
        {
            var loadoutReadOnly = Loadout.Load(Connection.Db, LoadoutId);
            var mutableCollection = loadoutReadOnly.MutableCollections().FirstOrDefault();
            if (mutableCollection == default)
            {
                Logger.LogWarning("No mutable collection found in loadout {LoadoutId}; creating a default 'My Mods' group", LoadoutId);
                using var createTx = Connection.BeginTransaction();
                _ = new CollectionGroup.New(createTx, out var newGroupId)
                {
                    IsReadOnly = false,
                    LoadoutItemGroup = new LoadoutItemGroup.New(createTx, newGroupId)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(createTx, newGroupId)
                        {
                            Name = "My Mods",
                            LoadoutId = LoadoutId,
                        },
                    },
                };
                var createResult = await createTx.Commit();
                ParentGroupId = LoadoutItemGroupId.From(createResult[newGroupId]);
            }
            else
            {
                ParentGroupId = LoadoutItemGroupId.From(mutableCollection.CollectionId);
            }
        }

        await context.YieldAsync();

        await RestoreMissingArchiveEntries(context.CancellationToken);

        var loadout = Loadout.Load(Connection.Db, LoadoutId);

        var installers = Installer is not null
            ? [Installer]
            : loadout.InstallationInstance.GetGame().LibraryItemInstallers;

        var result = await ExecuteInstallersAsync(installers, loadout, context);

        if (result == null)
        {
            if (Installer is AdvancedManualInstaller)
            {
                throw new InvalidOperationException($"Advanced installer did not succeed for `{LibraryItem.Name}` (`{LibraryItem.Id}`)");
            }

            var fallbackInstaller = FallbackInstaller ?? AdvancedManualInstaller.Create(ServiceProvider);
            result = await ExecuteInstallersAsync([fallbackInstaller], loadout, context);

            if (result == null)
            {
                throw new InvalidOperationException($"Found no installer that supports `{LibraryItem.Name}` (`{LibraryItem.Id}`), including the fallback installer!");
            }
        }

        // TODO(erri120): rename this entity to something unique, like "LoadoutItemInstalledFromLibrary"
        var loadoutGroup = result!;
        _ = new LibraryLinkedLoadoutItem.New(Transaction, loadoutGroup.Id)
        {
            LoadoutItemGroup = loadoutGroup,
            LibraryItemId = LibraryItem,
        };

        return new InstallLoadoutItemJobResult(null, loadoutGroup);
    }

    /// <summary>
    /// Installers read the item's files straight from the store; after a GC, Deep Clean or a manual wipe
    /// put them back from the original download first, so every install path gets re-extraction.
    /// </summary>
    private async Task RestoreMissingArchiveEntries(CancellationToken ct)
    {
        if (!LibraryItem.TryGetAsLibraryFile(out var file)) return;
        var hashes = file.TryGetAsLibraryArchive(out var archive)
            ? archive.Children.Select(child => child.AsLibraryFile().Hash).ToArray()
            : [file.Hash];

        var reExtractor = ServiceProvider.GetRequiredService<IDownloadReExtractor>();
        var fileStore = ServiceProvider.GetRequiredService<IFileStore>();
        var (missing, restored) = await reExtractor.RestoreMissingAsync(fileStore, hashes, ct);
        if (missing > 0)
            Logger.LogInformation("Faltaban {Missing} archivos de '{Name}' en el store; {Restored} reextraídos desde Descargas", missing, LibraryItem.Name, restored);
    }

    private async ValueTask<LoadoutItemGroup.New?> ExecuteInstallersAsync(
        ILibraryItemInstaller[] installers,
        Loadout.ReadOnly loadout,
        IJobContext<InstallLoadoutItemJob> context)
    {
        foreach (var installer in installers)
        {
            var isSupported = installer.IsSupportedLibraryItem(LibraryItem);
            if (!isSupported) continue;

            using var subTransaction = context.Definition.Transaction.CreateSubTransaction();
            
            var loadoutGroup = new LoadoutItemGroup.New(subTransaction, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(subTransaction, groupId)
                {
                    Name = LibraryItem.Name,
                    LoadoutId = LoadoutId,
                    ParentId = ParentGroupId.Value,
                },
            };

            // TODO(erri120): add safeguards to only allow groups to be added to the parent groups
            try
            {
                var result = await installer.ExecuteAsync(LibraryItem, loadoutGroup, subTransaction, loadout, context.CancellationToken);
                if (result.IsNotSupported(out var reason))
                {
                    if (Logger.IsEnabled(LogLevel.Trace) && !string.IsNullOrEmpty(reason))
                        Logger.LogTrace("Installer doesn't support library item `{LibraryItem}` because \"{Reason}\"", LibraryItem.Name, reason);
                    continue;
                }

                Debug.Assert(result.IsSuccess);
                subTransaction.CommitToParent();
                return loadoutGroup;
            }
            catch (OperationCanceledException ex)
            {
                context.CancelAndThrow(ex.Message);
            }
        }

        return null;
    }
}
