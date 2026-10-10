using Avalonia.Platform.Storage;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library;
using NexusMods.Sdk.Hashes;
using NexusMods.Paths;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Overlays;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk;
using R3;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.CollectionDownload;

public interface IManualDownloadRequiredOverlayViewModel : IOverlayViewModel
{
    string DownloadName { get; }
    string ExpectedHash { get; }
    string ExpectedSize { get; }

    bool HasInstructions { get; }
    string Instructions { get; }

    bool IsCheckingFile { get; }
    bool IsIncorrectFile { get; }
    string ReceivedHash { get; }

    ReactiveCommand CommandCancel { get; }
    ReactiveCommand CommandOpenBrowser { get; }
    ReactiveCommand CommandAddFile { get; }
    ReactiveCommand CommandTryAgain { get; }
    ReactiveCommand CommandReportBug { get; }
}

public class ManualDownloadRequiredOverlayDesignViewModel : AOverlayViewModel<IManualDownloadRequiredOverlayViewModel>, IManualDownloadRequiredOverlayViewModel
{
    public string DownloadName => "Mod Name";
    public string ExpectedHash => "123abc";
    public string ExpectedSize => "112 MB";
    public bool HasInstructions => true;
    public string Instructions => "Click the 3rd link down under the heading “Latest downloads”. Ignore the advert above it. Make sure the file size matches 112mb.";
    public bool IsCheckingFile => true;
    public bool IsIncorrectFile => false;

    public string ReceivedHash => "456def";
    public ReactiveCommand CommandCancel { get; } = new();
    public ReactiveCommand CommandOpenBrowser { get; } = new();
    public ReactiveCommand CommandAddFile { get; } = new();
    public ReactiveCommand CommandTryAgain { get; } = new();
    public ReactiveCommand CommandReportBug { get; } = new();
}

public class ManualDownloadRequiredOverlayViewModel : AOverlayViewModel<IManualDownloadRequiredOverlayViewModel>, IManualDownloadRequiredOverlayViewModel
{
    private static async Task<Md5Value> Md5Of(AbsolutePath file)
    {
        await using var stream = file.Read();
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Md5Value.From(await md5.ComputeHashAsync(stream));
    }

    public ManualDownloadRequiredOverlayViewModel(IServiceProvider serviceProvider, CollectionDownloadExternal.ReadOnly downloadEntity)
    {
        var osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        var avaloniaInterop = serviceProvider.GetRequiredService<IAvaloniaInterop>();
        var libraryService = serviceProvider.GetRequiredService<ILibraryService>();
        var logger = serviceProvider.GetRequiredService<ILogger<ManualDownloadRequiredOverlayViewModel>>();
        var mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();

        DownloadName = downloadEntity.AsCollectionDownload().Name;
        ExpectedHash = downloadEntity.Md5.ToString();
        ExpectedSize = ByteSize.FromBytes(downloadEntity.Size.Value).Humanize();

        HasInstructions = downloadEntity.AsCollectionDownload().Instructions.HasValue;
        Instructions = downloadEntity.AsCollectionDownload().Instructions.ValueOr(string.Empty);

        var gameDomain = mappingCache[downloadEntity.AsCollectionDownload().CollectionRevision.Collection.GameId];
        var revision = downloadEntity.AsCollectionDownload().CollectionRevision;
        var revisionBugsUri = NexusModsUrlBuilder.GetCollectionBugsUri(gameDomain, revision.Collection.Slug, revision.RevisionNumber, campaign: NexusModsUrlBuilder.CampaignCollections);

        CommandCancel = new ReactiveCommand(_ => { base.Close(); });
        CommandOpenBrowser = new ReactiveCommand(execute: _ => osInterop.OpenUri(downloadEntity.Uri));

        CommandAddFile = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                var paths = await avaloniaInterop.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = $"Browse file for download \"{downloadEntity.AsCollectionDownload().Name}\"",
                    AllowMultiple = false,
                });

                if (!paths.TryGetFirst(out var file)) return;

                IsCheckingFile = true;

                // Hash before adding: AddLocalFile reuses an existing library item with the same
                // content, so adding first and deleting on mismatch could delete something the user
                // already had (and left an orphan copy in Downloads on every wrong pick).
                var receivedHash = await Md5Of(file);
                ReceivedHash = receivedHash.ToString();

                if (receivedHash == downloadEntity.Md5)
                {
                    logger.LogInformation("Received file with matching hash for download `{DownloadName}` (index={Index})", downloadEntity.AsCollectionDownload().Name, downloadEntity.AsCollectionDownload().ArrayIndex);
                    await libraryService.AddLocalFile(file);
                    base.Close();
                    return;
                }

                logger.LogWarning("Received file with hash `{ActualHash}` that doesn't match expected hash of `{ExpectedHash}` for download `{DownloadName}` (index={Index})", receivedHash, downloadEntity.Md5, downloadEntity.AsCollectionDownload().Name, downloadEntity.AsCollectionDownload().ArrayIndex);

                IsCheckingFile = false;
                IsIncorrectFile = true;
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        CommandTryAgain = new ReactiveCommand(_ =>
        {
            // reset
            IsCheckingFile = false;
            IsIncorrectFile = false;
            ReceivedHash = string.Empty;
        });

        CommandReportBug = new ReactiveCommand(execute: _ => osInterop.OpenUri(revisionBugsUri));
    }

    public string DownloadName { get; }
    public string ExpectedHash { get; }
    public string ExpectedSize { get; }

    public bool HasInstructions { get; }
    public string Instructions { get; }

    [Reactive] public bool IsCheckingFile { get; private set; }
    [Reactive] public bool IsIncorrectFile { get; private set; }
    [Reactive] public string ReceivedHash { get; private set; } = string.Empty;

    public ReactiveCommand CommandCancel { get; }
    public ReactiveCommand CommandOpenBrowser { get; }
    public ReactiveCommand CommandAddFile { get; }
    public ReactiveCommand CommandTryAgain { get; }
    public ReactiveCommand CommandReportBug { get; }
}
