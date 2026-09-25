using Humanizer;
using JetBrains.Annotations;
using NexusMods.Abstractions.GC;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Pages.StorageManager.Dialogs;
using NexusMods.App.UI.Windows;
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
    public ReactiveCommand<Unit> RefreshCommand { get; }

    public StorageManagerPageViewModel(
        IWindowManager windowManager,
        IStorageAnalyzer storageAnalyzer,
        IGarbageCollectorRunner gcRunner) : base(windowManager)
    {
        TabTitle = "Storage Manager";
        TabIcon = IconValues.HardDrive;

        RefreshCommand = new ReactiveCommand<Unit>(async (_, ct) =>
        {
            await RefreshStatsAsync(storageAnalyzer, ct);
        });

        RunGarbageCollectionCommand = new ReactiveCommand<Unit>(async (_, ct) =>
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

        DeepCleanCommand = new ReactiveCommand<Unit>(async (_, ct) =>
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
                await storageAnalyzer.DeletePhysicalFilesAsync(ct);
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

        DeleteDownloadsCommand = new ReactiveCommand<Unit>(async (_, ct) =>
        {
            if (IsBusy) return;

            var dialog = DialogFactory.CreateStandardDialog(
                title: "Borrar descargas",
                new StandardDialogParameters
                {
                    Text = "Se borran los archivos originales de tModManager/Downloads. Vas a tener que volver a bajarlos para reinstalar o reconstruir el store.",
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
