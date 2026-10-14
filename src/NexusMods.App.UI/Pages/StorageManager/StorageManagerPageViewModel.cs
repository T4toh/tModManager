using System.Reactive.Linq;
using Humanizer;
using JetBrains.Annotations;
using NexusMods.Abstractions.GC;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Helpers;
using NexusMods.App.UI.Pages.StorageManager.Dialogs;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.Pages.MyGames.WinePrefix;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.DataModel.Storage;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;
using NexusMods.UI.Sdk.Icons;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using CompositeDisposable = System.Reactive.Disposables.CompositeDisposable;

namespace NexusMods.App.UI.Pages.StorageManager;

[UsedImplicitly]
internal class StorageManagerPageViewModel : APageViewModel<IStorageManagerPageViewModel>, IStorageManagerPageViewModel
{
    [Reactive] public string ArchivesSizeText { get; private set; } = "—";
    [Reactive] public int BackedUpFilesCount { get; private set; }
    [Reactive] public string DownloadsFolderSizeText { get; private set; } = "—";
    [Reactive] public string CyberpunkBackupsSizeText { get; private set; } = "—";
    [Reactive] public bool IsBusy { get; private set; }

    public ReactiveCommand<Unit> RunGarbageCollectionCommand { get; }
    public ReactiveCommand<Unit> DeepCleanCommand { get; }
    public ReactiveCommand<Unit> DeleteDownloadsCommand { get; }
    public ReactiveCommand<Unit> DeleteProtonPrefixCommand { get; }
    public ReactiveCommand<Unit> RefreshCommand { get; }

    public StorageManagerPageViewModel(
        IWindowManager windowManager,
        IStorageAnalyzer storageAnalyzer,
        IGarbageCollectorRunner gcRunner,
        IServiceProvider serviceProvider) : base(windowManager)
    {
        TabTitle = "Storage Manager";
        TabIcon = IconValues.HardDrive;

        RefreshCommand = new ReactiveCommand<Unit>(async (_, ct) =>
        {
            await RefreshStatsAsync(storageAnalyzer, ct);
        });

        // One storage operation at a time: the buttons stay disabled while one runs
        var notBusy = this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy).ToObservable();

        RunGarbageCollectionCommand = notBusy.ToReactiveCommand<Unit>(async (_, ct) =>
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await gcRunner.RunAsync();
                await RefreshStatsAsync(storageAnalyzer, ct);
            }
            finally
            {
                IsBusy = false;
            }
        });

        DeepCleanCommand = notBusy.ToReactiveCommand<Unit>(async (_, ct) =>
        {
            if (IsBusy) return;

            // Show confirmation dialog with Super Clean checkbox
            var contentVm = new DeepCleanDialogContentViewModel();
            var dialog = DialogFactory.CreateDialog(
                title: "Confirm Deep Clean",
                buttonDefinitions:
                [
                    new DialogButtonDefinition("Cancel", ButtonDefinitionId.Cancel, ButtonAction.Reject),
                    new DialogButtonDefinition("Deep Clean", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive),
                ],
                contentViewModel: contentVm,
                dialogWindowSize: DialogWindowSize.Medium
            );

            var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (result.ButtonId != ButtonDefinitionId.Accept) return;

            var nukeMode = contentVm.NukeMode;

            IsBusy = true;
            try
            {
                if (nukeMode)
                    await storageAnalyzer.RunDeepCleanOnAllLoadoutsAsync(ct);
                await storageAnalyzer.DeleteAllBackedUpFilesAsync(ct);
                // Super Clean just moved the mod files into a new snapshot: keep it, drop the older ones.
                await storageAnalyzer.DeletePhysicalFilesAsync(keepNewest: nukeMode, ct);
                if (nukeMode)
                    await storageAnalyzer.DeleteArchivesAsync(ct);
                await gcRunner.RunAsync();
                await RefreshStatsAsync(storageAnalyzer, ct);
            }
            finally
            {
                IsBusy = false;
            }
        });

        DeleteDownloadsCommand = notBusy.ToReactiveCommand<Unit>(async (_, ct) =>
        {
            if (IsBusy) return;

            var dialog = DialogFactory.CreateStandardDialog(
                title: "Borrar descargas",
                new StandardDialogParameters
                {
                    Text = "Se borran las descargas originales, de todos los juegos. En la carpeta de descargas de tModManager se borra todo; si elegiste otra carpeta, solo las descargas que tModManager registró (otros archivos de esa carpeta no se tocan). Vas a tener que volver a bajarlas para reinstalar o reconstruir el store.",
                },
                buttonDefinitions:
                [
                    new DialogButtonDefinition("Cancelar", ButtonDefinitionId.Cancel, ButtonAction.Reject),
                    new DialogButtonDefinition("Borrar", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive),
                ]
            );

            var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (result.ButtonId != ButtonDefinitionId.Accept) return;

            IsBusy = true;
            try
            {
                await storageAnalyzer.DeleteDownloadsAsync(ct);
                await RefreshStatsAsync(storageAnalyzer, ct);
            }
            finally
            {
                IsBusy = false;
            }
        });

        DeleteProtonPrefixCommand = notBusy.ToReactiveCommand<Unit>(async (_, ct) =>
        {
            if (IsBusy) return;

            var installations = SteamPaths.Installations(serviceProvider);
            if (installations.Length == 0)
            {
                await windowManager.ShowDialog(
                    DialogFactory.CreateStandardDialog(
                        title: "Prefix de Proton",
                        new StandardDialogParameters { Text = "No hay juegos de Steam gestionados." },
                        buttonDefinitions: [DialogStandardButtons.Ok]
                    ),
                    DialogWindowType.Modal
                );
                return;
            }

            var installation = installations[0];
            if (installations.Length > 1)
            {
                var pick = await windowManager.ShowDialog(
                    DialogFactory.CreateStandardDialog(
                        title: "Borrar prefix de Proton",
                        new StandardDialogParameters { Text = "¿De qué juego?" },
                        buttonDefinitions:
                        [
                            new DialogButtonDefinition("Cancelar", ButtonDefinitionId.Cancel, ButtonAction.Reject),
                            ..installations.Select((install, i) => new DialogButtonDefinition(install.Game.DisplayName, ButtonDefinitionId.From($"game-{i}"), ButtonAction.Accept)),
                        ]
                    ),
                    DialogWindowType.Modal
                );
                var picked = Enumerable.Range(0, installations.Length).FirstOrDefault(i => pick.ButtonId == ButtonDefinitionId.From($"game-{i}"), -1);
                if (picked < 0) return;
                installation = installations[picked];
            }

            // The prefix comes back without the packages the game needs; the Wine prefix panel in My Games installs them
            var packages = installation.Game.WinePrefixRequirements?.WinetricksPackages ?? [];
            var reinstallNote = packages.Length > 0
                ? $"\n\nDespués, cuando Steam lo recree, hay que volver a instalar {string.Join(" y ", packages.Order(StringComparer.Ordinal))}: el panel \"Wine prefix\" de Mis juegos lo detecta y los instala con un botón."
                : "";
            var dialog = DialogFactory.CreateStandardDialog(
                title: "Borrar prefix de Proton",
                new StandardDialogParameters
                {
                    Text = $"Se borra steamapps/compatdata/{installation.LocatorResult.StoreIdentifier} ({installation.Game.DisplayName}). Steam lo recrea al lanzar el juego. Se pierden los saves que no estén sincronizados con la nube y toda la configuración del prefix. Cerrá el juego antes de continuar.{reinstallNote}",
                },
                buttonDefinitions:
                [
                    new DialogButtonDefinition("Cancelar", ButtonDefinitionId.Cancel, ButtonAction.Reject),
                    new DialogButtonDefinition("Borrar", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive),
                ]
            );

            var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (result.ButtonId != ButtonDefinitionId.Accept) return;

            IsBusy = true;
            try
            {
                await storageAnalyzer.DeleteProtonPrefixAsync(installation, ct);
                await RefreshStatsAsync(storageAnalyzer, ct);
            }
            finally
            {
                IsBusy = false;
            }
        });

        this.WhenActivated((CompositeDisposable _) =>
        {
            RefreshCommand.Execute(Unit.Default);
        });
    }

    private async Task RefreshStatsAsync(IStorageAnalyzer analyzer, CancellationToken ct)
    {
        var stats = await analyzer.GetStorageStatsAsync(ct);
        ArchivesSizeText = ByteSize.FromBytes((double)stats.ArchivesSize.Value).ToString();
        BackedUpFilesCount = stats.BackedUpFilesCount;
        DownloadsFolderSizeText = ByteSize.FromBytes((double)stats.DownloadsFolderSize.Value).ToString();
        CyberpunkBackupsSizeText = ByteSize.FromBytes((double)stats.CyberpunkBackupsSize.Value).ToString();
    }
}
