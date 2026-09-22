using System.ComponentModel;
using System.Reactive.Linq;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Sdk.Library;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Controls.MarkdownRenderer;
using NexusMods.App.UI.Controls.Navigation;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Helpers;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.App.UI.Pages.LoadoutPage;
using NexusMods.App.UI.Pages.TextEdit;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Settings;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Collections;
using NexusMods.UI.Sdk.Icons;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Abstractions.Downloads;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Dialog;
using OneOf;
using Microsoft.Extensions.Logging;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Observable = System.Reactive.Linq.Observable;
using ReactiveCommand = R3.ReactiveCommand;

namespace NexusMods.App.UI.Pages.CollectionDownload;

using CollectionDownloadEntity = NexusMods.Abstractions.NexusModsLibrary.Models.CollectionDownload;

public sealed class CollectionDownloadViewModel : APageViewModel<ICollectionDownloadViewModel>, ICollectionDownloadViewModel
{
    private readonly CollectionRevisionMetadata.ReadOnly _revision;
    private readonly CollectionMetadata.ReadOnly _collection;

    private readonly IServiceProvider _serviceProvider;
    private readonly IOverlayController _overlayController;
    private readonly IWindowNotificationService _notificationService;
    private readonly Optional<LoadoutId> _targetLoadout;
    private readonly ILogger<CollectionDownloadViewModel> _logger;

    public CollectionDownloadTreeDataGridAdapter TreeDataGridAdapter { get; }

    public CollectionDownloadViewModel(
        IWindowManager windowManager,
        IServiceProvider serviceProvider,
        CollectionRevisionMetadata.ReadOnly revisionMetadata,
        Optional<LoadoutId> targetLoadout) : base(windowManager)
    {
        _serviceProvider = serviceProvider;
        _overlayController = serviceProvider.GetRequiredService<IOverlayController>();
        _notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();
        _logger = serviceProvider.GetRequiredService<ILogger<CollectionDownloadViewModel>>();

        var connection = serviceProvider.GetRequiredService<IConnection>();
        var downloadsService = serviceProvider.GetRequiredService<IDownloadsService>();
        var mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();
        var osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        var avaloniaInterop = serviceProvider.GetRequiredService<IAvaloniaInterop>();
        var nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var collectionDownloader = serviceProvider.GetRequiredService<CollectionDownloader>();
        var loginManager = serviceProvider.GetRequiredService<ILoginManager>();
        var jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();

        var tileImagePipeline = ImagePipelines.GetCollectionTileImagePipeline(serviceProvider);
        var backgroundImagePipeline = ImagePipelines.GetCollectionBackgroundImagePipeline(serviceProvider);
        var userAvatarPipeline = ImagePipelines.GetUserAvatarPipeline(serviceProvider);

        _revision = revisionMetadata;
        _collection = revisionMetadata.Collection;
        _targetLoadout = targetLoadout;

        // Safely look up the library file — it may be missing if archives were deleted manually.
        // The install job handles re-downloading if it's missing or its archive is gone.
        NexusModsCollectionLibraryFile.ReadOnly libraryFile;
        LibraryArchiveFileEntry.ReadOnly collectionJsonFile;
        try
        {
            libraryFile = collectionDownloader.GetLibraryFile(revisionMetadata);
            collectionJsonFile = nexusModsLibrary.GetCollectionJsonFile(libraryFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Collection library file missing for '{Name}'; install will trigger automatic re-download", revisionMetadata.Collection.Name);
            libraryFile = default;
            collectionJsonFile = default;
        }

        TabTitle = _collection.Name;
        TabIcon = IconValues.CollectionsOutline;

        TreeDataGridAdapter = new CollectionDownloadTreeDataGridAdapter(serviceProvider, revisionMetadata, _targetLoadout);
        TreeDataGridAdapter.ViewHierarchical.Value = false;

        RequiredDownloadsCount = CollectionDownloader.CountItems(_revision, CollectionDownloader.ItemType.Required);
        OptionalDownloadsCount = CollectionDownloader.CountItems(_revision, CollectionDownloader.ItemType.Optional);
        InstallRequiredItemsText = RequiredDownloadsCount > 0 && OptionalDownloadsCount > 0
            ? Language.CollectionDownloadViewModel_InstallRequired
            : Language.CollectionDownloadViewModel_InstallCollection;

        _logger.LogDebug("[INSTALL-BTN] Collection '{Name}' - RequiredDownloadsCount={Required}, OptionalDownloadsCount={Optional}, TotalDownloads={Total}",
            _collection.Name, RequiredDownloadsCount, OptionalDownloadsCount, _revision.Downloads.Count());

        var canInstall = R3.Observable.Return(_targetLoadout.HasValue);

        CommandDownloadRequiredItems = R3.Observable.CombineLatest(_isDownloadingRequiredItems, _canDownloadRequiredItems, static (isDownloading, canDownload) => !isDownloading && canDownload)
            .ToReactiveCommand<Unit>(
                executeAsync: async (_, cancellationToken) =>
                {
                    if (!await loginManager.EnsureLoggedIn("Download Collection", cancellationToken)) return;

                    await collectionDownloader.DownloadItems(_revision, itemType: CollectionDownloader.ItemType.Required, db: connection.Db,
                        cancellationToken: cancellationToken
                    );
                },
                awaitOperation: AwaitOperation.Drop,
                configureAwait: false
            );

        CommandDownloadOptionalItems = R3.Observable.CombineLatest(_isDownloadingOptionalItems, _canDownloadOptionalItems, static (isDownloading, canDownload) => !isDownloading && canDownload)
            .ToReactiveCommand<Unit>(
                executeAsync: async (_, cancellationToken) =>
                {
                    await collectionDownloader.DownloadItems(_revision, itemType: CollectionDownloader.ItemType.Optional, db: connection.Db,
                        cancellationToken: cancellationToken
                    );
                },
                awaitOperation: AwaitOperation.Drop,
                configureAwait: false
            );

        CommandInstallOptionalItems = R3.Observable.CombineLatest(IsInstalling, _canInstallOptionalItems, canInstall, static (isInstalling, canInstallItems, hasLoadout) => !isInstalling && canInstallItems && hasLoadout)
            .ToReactiveCommand<Unit>(
                executeAsync: async (_, _) =>
                {
                    if (!await CollectionConflictHelpers.ConfirmInstallWithConflicts(
                            connection.Db, _targetLoadout.Value, _collection, WindowManager))
                        return;

                    // Only install optional items that have actually been downloaded
                    var downloadedItems = CollectionDownloader.GetItems(revisionMetadata, CollectionDownloader.ItemType.Optional)
                        .Where(d => CollectionDownloader.GetStatus(d, connection.Db).IsDownloaded())
                        .ToArray();
                    await InstallCollectionJob.Create(
                        serviceProvider,
                        _targetLoadout.Value,
                        source: libraryFile,
                        revisionMetadata,
                        items: downloadedItems
                    );
                },
                awaitOperation: AwaitOperation.Drop,
                configureAwait: false
            );

        CommandInstallRequiredItems = R3.Observable.CombineLatest(IsInstalling, _canInstallRequiredItems, canInstall, static (isInstalling, canInstallItems, hasLoadout) => !isInstalling && canInstallItems && hasLoadout)
            .ToReactiveCommand<Unit>(
                executeAsync: async (_, _) =>
                {
                    if (!await CollectionConflictHelpers.ConfirmInstallWithConflicts(
                            connection.Db, _targetLoadout.Value, _collection, WindowManager))
                        return;

                    var installItemType = RequiredDownloadsCount > 0 ? CollectionDownloader.ItemType.Required : CollectionDownloader.ItemType.Optional;
                    // For all-optional collections, only install items that have been downloaded
                    var items = CollectionDownloader.GetItems(revisionMetadata, installItemType)
                        .Where(d => CollectionDownloader.GetStatus(d, connection.Db).IsDownloaded())
                        .ToArray();
                    var group = await InstallCollectionJob.Create(
                        serviceProvider,
                        _targetLoadout.Value,
                        source: libraryFile,
                        revisionMetadata,
                        items: items
                    );

                    if (CollectionDownloader.IsFullyInstalled(items, group.AsCollectionGroup(), connection.Db))
                        _notificationService.ShowToast(Language.ToastNotification_Collection_installed, ToastNotificationVariant.Success);
                },
                awaitOperation: AwaitOperation.Drop,
                configureAwait: false
            );

        CommandDeleteCollectionRevision = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                var pageData = new PageData
                {
                    FactoryId = LibraryPageFactory.StaticId,
                    Context = new LibraryPageContext()
                    {
                        LoadoutId = _targetLoadout.ValueOr(Initializers.LoadoutId),
                    },
                };

                var workspaceController = GetWorkspaceController();
                var behavior = new OpenPageBehavior.ReplaceTab(PanelId, TabId);
                workspaceController.OpenPage(WorkspaceId, pageData, behavior,
                    checkOtherPanels: false
                );

                await collectionDownloader.DeleteCollectionLoadoutGroup(_revision, cancellationToken: CancellationToken.None);
                await collectionDownloader.DeleteRevision(_revision);
                
                _notificationService.ShowToast(Language.ToastNotification_Collection_deleted);
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false,
            cancelOnCompleted: false
        );

        ModList = _revision.Downloads.Select(download =>
        {
            var source = "Unknown";
            var link = string.Empty;
            var hash = string.Empty;

            if (download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload))
            {
                source = "Nexus Mods";
                var domain = mappingCache[nexusModsDownload.FileUid.GameId];
                link = NexusModsUrlBuilder.GetFileDownloadUri(domain, nexusModsDownload.ModUid.ModId, nexusModsDownload.FileUid.FileId, useNxmLink: false, source: null).ToString();
                hash = $"FileId: {nexusModsDownload.FileUid.FileId}";
            }
            else if (download.TryGetAsCollectionDownloadExternal(out var externalDownload))
            {
                source = "External";
                link = externalDownload.Uri.ToString();
                hash = $"MD5: {externalDownload.Md5}";
            }
            else if (download.TryGetAsCollectionDownloadBundled(out _))
            {
                source = "Bundled";
            }

            return new ModDetail(download.Name, source, link, hash);
        }).ToArray();

        CommandCopyModList = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var mod in ModList)
                {
                    sb.AppendLine($"{mod.Name} ({mod.Source})");
                    if (!string.IsNullOrEmpty(mod.Link)) sb.AppendLine($"  Link: {mod.Link}");
                    if (!string.IsNullOrEmpty(mod.Hash)) sb.AppendLine($"  Hash: {mod.Hash}");
                    sb.AppendLine();
                }
                await avaloniaInterop.SetClipboardTextAsync(sb.ToString());
                _notificationService.ShowToast("Mod list copied to clipboard", ToastNotificationVariant.Success);
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        CommandDeleteAllDownloads = new ReactiveCommand(canExecuteSource: R3.Observable.Return(false), initialCanExecute: false);

        CommandViewOnNexusMods = new ReactiveCommand(execute: _ =>
        {
            var gameDomain = mappingCache[_collection.GameId];
            var uri = NexusModsUrlBuilder.GetCollectionUri(gameDomain, _collection.Slug, revisionMetadata.RevisionNumber, campaign: NexusModsUrlBuilder.CampaignCollections);
            osInterop.OpenUri(uri);
        });

        CommandOpenJsonFile = new ReactiveCommand(
            execute: _ =>
            {
                if (!collectionJsonFile.IsValid()) return;
                var pageData = new PageData
                {
                    FactoryId = TextEditorPageFactory.StaticId,
                    Context = new TextEditorPageContext
                    {
                        FileId = collectionJsonFile.AsLibraryFile().LibraryFileId,
                        FilePath = collectionJsonFile.AsLibraryFile().FileName,
                        IsReadOnly = true,
                    },
                };

                var workspaceController = GetWorkspaceController();
                var behavior = new OpenPageBehavior.NewTab(PanelId);
                workspaceController.OpenPage(WorkspaceId, pageData, behavior);
            }
        );

        CommandViewCollection = IsInstalled
            .ToReactiveCommand<NavigationInformation>(info =>
                {
                    var group = CollectionDownloader.GetCollectionGroup(_revision, _targetLoadout, connection.Db).Value;

                    var pageData = new PageData
                    {
                        FactoryId = CollectionLoadoutPageFactory.StaticId,
                        Context = new CollectionLoadoutPageContext
                        {
                            LoadoutId = _targetLoadout.Value,
                            GroupId = group.AsCollectionGroup(),
                        },
                    };

                    var workspaceController = GetWorkspaceController();
                    var behavior = workspaceController.GetOpenPageBehavior(pageData, info);
                    workspaceController.OpenPage(WorkspaceId, pageData, behavior);
                }
            );

        IsDownloading = R3.Observable.CombineLatest(_isDownloadingRequiredItems, _isDownloadingOptionalItems, static (a, b) => a || b).ToBindableReactiveProperty();
        IsUpdateAvailable = NewestRevisionNumber.Select(static optional => optional.HasValue).ToBindableReactiveProperty();

        CommandUpdateCollection = IsUpdateAvailable
            .ToReactiveCommand<Unit>(
                executeAsync: async (_, cancellationToken) =>
                {
                    var newestRevisionNumber = NewestRevisionNumber.Value.Value;
                    var revision = await collectionDownloader.GetOrAddRevision(_collection.Slug, newestRevisionNumber, cancellationToken);

                    var pageData = new PageData
                    {
                        FactoryId = CollectionDownloadPageFactory.StaticId,
                        Context = new CollectionDownloadPageContext
                        {
                            TargetLoadout = _targetLoadout,
                            CollectionRevisionMetadataId = revision,
                        },
                    };

                                    var workspaceController = GetWorkspaceController();

                                    workspaceController.OpenPage(WorkspaceId, pageData, new OpenPageBehavior.ReplaceTab(PanelId, TabId));

                                }, awaitOperation: AwaitOperation.Drop, configureAwait: false

                            );

                    

                            CommandRescanDownloads = R3.Observable.CombineLatest(
                                    IsDownloading,
                                    IsInstalling,
                                    _isRescanning,
                                    static (downloading, installing, rescanning) => !downloading && !installing && !rescanning)
                                .ToReactiveCommand<Unit>(
                                executeAsync: async (_, cancellationToken) =>
                                {
                                    _isRescanning.OnNext(true);
                                    try
                                    {
                                        await collectionDownloader.RescanDownloads(_revision, cancellationToken);
                                        _notificationService.ShowToast("Rescan complete", ToastNotificationVariant.Success);
                                    }
                                    finally
                                    {
                                        _isRescanning.OnNext(false);
                                    }
                                },
                                awaitOperation: AwaitOperation.Drop,
                                configureAwait: false
                            );

                    

                            this.WhenActivated(disposables =>

                    
            {
                // Show loading while the adapter initializes its data source
                IsLoading = true;

                TreeDataGridAdapter.Activate().AddTo(disposables);

                // Clear loading once the adapter's data source is ready
                TreeDataGridAdapter.IsSourceEmpty
                    .Take(1)
                    .Subscribe(_ => IsLoading = false)
                    .AddTo(disposables);

                jobMonitor
                    .HasActiveJob<InstallCollectionJob>(job => job.RevisionMetadata.Id == _revision.Id)
                    .OnUI()
                    .Subscribe(isInstalling => IsInstalling.Value = isInstalling)
                    .AddTo(disposables);

                jobMonitor
                    .HasActiveJob<DownloadCollectionJob>(job => job.RevisionMetadata.Id == _revision.Id && job.ItemType == CollectionDownloader.ItemType.Required)
                    .OnUI()
                    .Subscribe(isDownloading => _isDownloadingRequiredItems.OnNext(isDownloading))
                    .AddTo(disposables);

                jobMonitor
                    .HasActiveJob<DownloadCollectionJob>(job => job.RevisionMetadata.Id == _revision.Id && job.ItemType == CollectionDownloader.ItemType.Optional)
                    .OnUI()
                    .Subscribe(isDownloading => _isDownloadingOptionalItems.OnNext(isDownloading))
                    .AddTo(disposables);

                var numDownloadedRequiredItemsObservable = Observable
                    .Return(_revision)
                    .OffUi()
                    .SelectMany(revision => collectionDownloader.DownloadedItemCountObservable(revision, itemType: CollectionDownloader.ItemType.Required));

                var numDownloadedOptionalItemsObservable = Observable
                    .Return(_revision)
                    .OffUi()
                    .SelectMany(revision => collectionDownloader.DownloadedItemCountObservable(revision, itemType: CollectionDownloader.ItemType.Optional));

                loginManager.IsLoggedInObservable
                    .Prepend(false)
                    .OnUI()
                    .Subscribe(isLoggedIn => CanDownloadAutomatically = isLoggedIn)
                    .AddTo(disposables);

                var collectionGroupObservable = collectionDownloader.GetCollectionGroupObservable(_revision, _targetLoadout);
                var isCollectionInstalledObservable = collectionDownloader
                    .IsCollectionInstalledObservable(_revision, collectionGroupObservable)
                    .Prepend(false);
                var hasInstalledAllOptionalItems = collectionDownloader
                    .IsCollectionInstalledObservable(_revision, collectionGroupObservable, CollectionDownloader.ItemType.Optional)
                    .Prepend(false);

                numDownloadedRequiredItemsObservable.CombineLatest(isCollectionInstalledObservable, numDownloadedOptionalItemsObservable,
                        (req, installed, opt) => (req, installed, opt))
                    .OnUI()
                    .Subscribe(tuple =>
                        {
                            var (numDownloadedRequiredItems, isCollectionInstalled, numDownloadedOptionalItems) = tuple;
                            // Allow install as long as at least one required item is downloaded.
                            // For all-optional collections (RequiredDownloadsCount == 0), allow when at least 1 optional downloaded.
                            var hasAnyDownloadedItems = RequiredDownloadsCount > 0
                                ? numDownloadedRequiredItems > 0
                                : numDownloadedOptionalItems > 0;

                            _logger.LogDebug("[INSTALL-BTN] numDownloaded={Downloaded}, required={Required}, hasAll={HasAll}, isInstalled={IsInstalled} → canInstall={CanInstall}",
                                numDownloadedRequiredItems, RequiredDownloadsCount, hasAnyDownloadedItems, isCollectionInstalled, hasAnyDownloadedItems);

                            CountDownloadedRequiredItems = numDownloadedRequiredItems;
                            _canInstallRequiredItems.OnNext(hasAnyDownloadedItems);
                            _canDownloadRequiredItems.OnNext(RequiredDownloadsCount > 0 && numDownloadedRequiredItems < RequiredDownloadsCount);

                            var allRequiredDownloaded = RequiredDownloadsCount > 0
                                ? numDownloadedRequiredItems == RequiredDownloadsCount
                                : numDownloadedOptionalItems > 0;

                            if (allRequiredDownloaded && isCollectionInstalled)
                            {
                                if (!IsInstalled.Value) IsInstalled.Value = true;
                                CollectionStatusText = Language.CollectionDownloadViewModel_CollectionDownloadViewModel_Ready_to_play___All_required_mods_installed;
                            }
                            else if (allRequiredDownloaded)
                            {
                                IsInstalled.Value = false;
                                CollectionStatusText = Language.CollectionDownloadViewModel_Ready_to_install;
                            }
                            else
                            {
                                IsInstalled.Value = false;
                                if (RequiredDownloadsCount > 0)
                                    CollectionStatusText = string.Format(Language.CollectionDownloadViewModel_Num_required_mods_downloaded, numDownloadedRequiredItems, RequiredDownloadsCount);
                                else
                                    CollectionStatusText = string.Format(Language.CollectionDownloadViewModel_Num_required_mods_downloaded, numDownloadedOptionalItems, OptionalDownloadsCount);
                            }
                        }
                    ).AddTo(disposables);

                numDownloadedOptionalItemsObservable
                    .CombineLatest(hasInstalledAllOptionalItems)
                    .OnUI()
                    .Subscribe(tuple =>
                        {
                            var (numDownloadedOptionalItems, hasInstalledAllOptionals) = tuple;
                            var hasDownloadedAllOptionalItems = numDownloadedOptionalItems == OptionalDownloadsCount;

                            CountDownloadedOptionalItems = numDownloadedOptionalItems;
                            HasInstalledAllOptionalItems.Value = hasInstalledAllOptionals;
                            // Disable install button while downloads are in progress to avoid partial installs.
                            _canInstallOptionalItems.OnNext(numDownloadedOptionalItems > 0 && !hasInstalledAllOptionals && !_isDownloadingOptionalItems.Value);
                            _canDownloadOptionalItems.OnNext(numDownloadedOptionalItems < OptionalDownloadsCount);
                        }
                    ).AddTo(disposables);

                // Re-evaluate install-optional canExecute whenever download state changes.
                _isDownloadingOptionalItems
                    .Subscribe(isDownloading =>
                    {
                        _canInstallOptionalItems.OnNext(CountDownloadedOptionalItems > 0 && !(bool)HasInstalledAllOptionalItems.Value && !isDownloading);
                    }).AddTo(disposables);

                ImagePipelines.CreateObservable(_collection.Id, tileImagePipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (bitmap, self) => self.TileImage = bitmap)
                    .AddTo(disposables);

                ImagePipelines.CreateObservable(_collection.Id, backgroundImagePipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (bitmap, self) => self.BackgroundImage = bitmap)
                    .AddTo(disposables);

                ImagePipelines.CreateObservable(_collection.Author.Id, userAvatarPipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (bitmap, self) => self.AuthorAvatar = bitmap)
                    .AddTo(disposables);

                // Materialize active downloads cache for synchronous lookups in download control handlers
                var activeDownloadsCache = downloadsService.ActiveDownloads.AsObservableCache();
                disposables.Add(activeDownloadsCache);

                TreeDataGridAdapter.MessageSubject.SubscribeAwait(
                    onNextAsync: (message, cancellationToken) =>
                    {
                        return message.Match(
                            f0: installMessage => InstallItem(installMessage.DownloadEntity, cancellationToken),
                            f1: downloadNexusMods => collectionDownloader.Download(downloadNexusMods.DownloadEntity, cancellationToken),
                            f2: downloadExternal => collectionDownloader.Download(downloadExternal.DownloadEntity, cancellationToken),
                            f3: manualDownloadOpenModal => OpenManualDownloadModal(manualDownloadOpenModal.DownloadEntity),
                            f4: pauseMsg =>
                            {
                                HandleDownloadControl(pauseMsg.EntityId, connection, activeDownloadsCache,
                                    static (svc, info) => svc.PauseDownload(info), downloadsService);
                                return ValueTask.CompletedTask;
                            },
                            f5: resumeMsg =>
                            {
                                HandleDownloadControl(resumeMsg.EntityId, connection, activeDownloadsCache,
                                    static (svc, info) => svc.ResumeDownload(info), downloadsService);
                                return ValueTask.CompletedTask;
                            },
                            f6: cancelMsg =>
                            {
                                HandleDownloadControl(cancelMsg.EntityId, connection, activeDownloadsCache,
                                    static (svc, info) => svc.CancelDownload(info), downloadsService);
                                return ValueTask.CompletedTask;
                            },
                            f7: viewModPageMsg =>
                            {
                                OpenModPage(viewModPageMsg.EntityId, connection, osInterop);
                                return ValueTask.CompletedTask;
                            }
                        );
                    },
                    awaitOperation: AwaitOperation.Parallel,
                    configureAwait: false
                ).AddTo(disposables);

                // Re-sort the collection list when active downloads change (start/complete).
                // Debounce to ensure progress components have been added/removed before sorting.
                downloadsService.ActiveDownloads
                    .Throttle(TimeSpan.FromMilliseconds(300))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => TreeDataGridAdapter.ReapplyCurrentSort())
                    .AddTo(disposables);

                R3.Observable.Return(_revision)
                    .ObserveOnThreadPool()
                    .SelectAwait((revision, cancellationToken) => nexusModsLibrary.GetLastPublishedRevisionNumber(revision.Collection, cancellationToken))
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (graphQlResult, self) =>
                        {
                            if (!graphQlResult.TryGetData(out var lastPublishedRevisionNumber))
                            {
                                self._logger.LogWarning("No se pudo consultar la última revisión publicada de la colección '{Name}': {Errors}",
                                    self._revision.Collection.Name, string.Join("; ", graphQlResult.Errors.Select(kv => kv.Value.Message)));
                                return;
                            }
                            if (!lastPublishedRevisionNumber.HasValue)
                            {
                                self.IsUpdateAvailable.Value = false;
                                self.NewestRevisionNumber.Value = Optional<RevisionNumber>.None;
                            }
                            else
                            {
                                var isUpdateAvailable = lastPublishedRevisionNumber.Value > self._revision.RevisionNumber;

                                self.IsUpdateAvailable.Value = isUpdateAvailable;
                                self.NewestRevisionNumber.Value = isUpdateAvailable ? lastPublishedRevisionNumber : Optional<RevisionNumber>.None;
                            }
                        }
                    ).AddTo(disposables);

                // Only parse the collection JSON if the archive is present (it may be missing after manual deletion)
                if (collectionJsonFile.IsValid())
                {
                R3.Observable.Return(collectionJsonFile)
                    .ObserveOnThreadPool()
                    .SelectAwait((jsonFile, cancellationToken) => nexusModsLibrary.ParseCollectionJsonFile(jsonFile, cancellationToken))
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe((this, serviceProvider), static (collectionRoot, state) =>
                        {
                            var (self, serviceProvider) = state;

                            var collectionInstructionsText = collectionRoot.Info.InstallInstructions;

                            var modsInstructions = collectionRoot.Mods
                                .Select(static mod => (mod.Name, mod.Instructions, mod.Optional))
                                .Where(static tuple => !string.IsNullOrWhiteSpace(tuple.Instructions))
                                .Select(static tuple =>
                                    new ModInstructions(tuple.Name, tuple.Instructions, tuple.Optional ? CollectionDownloader.ItemType.Optional : CollectionDownloader.ItemType.Required)
                                )
                                .ToArray();

                            var optionalModsInstructions = modsInstructions.Where(static x => x.ItemType == CollectionDownloader.ItemType.Optional).ToArray();
                            var requiredModsInstructions = modsInstructions.Where(static x => x.ItemType == CollectionDownloader.ItemType.Required).ToArray();

                            if (!string.IsNullOrWhiteSpace(collectionInstructionsText))
                            {
                                var markdownRendererViewModel = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
                                markdownRendererViewModel.Contents = collectionInstructionsText;
                                self.InstructionsRenderer = markdownRendererViewModel;
                            }

                            self.RequiredModsInstructions = requiredModsInstructions;
                            self.OptionalModsInstructions = optionalModsInstructions;
                        }
                    ).AddTo(disposables);
                }
            }
        );
    }

    private ValueTask OpenManualDownloadModal(CollectionDownloadExternal.ReadOnly downloadEntity)
    {
        _overlayController.Enqueue(new ManualDownloadRequiredOverlayViewModel(_serviceProvider, downloadEntity));
        return ValueTask.CompletedTask;
    }

    private async ValueTask InstallItem(CollectionDownloadEntity.ReadOnly download, CancellationToken cancellationToken)
    {
        if (!_targetLoadout.HasValue) return;

        var monitor = _serviceProvider.GetRequiredService<IJobMonitor>();

        var job = await InstallCollectionDownloadJob.Create(
            serviceProvider: _serviceProvider,
            targetLoadout: _targetLoadout.Value,
            download: download,
            cancellationToken: cancellationToken
        );

        await monitor.Begin<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly>(job);
    }

    private static void HandleDownloadControl(
        EntityId collectionDownloadEntityId,
        IConnection connection,
        IObservableCache<DownloadInfo, DownloadId> activeDownloadsCache,
        Action<IDownloadsService, DownloadInfo> action,
        IDownloadsService downloadsService)
    {
        var db = connection.Db;
        if (!CollectionDownloadNexusMods.TryGet(db, collectionDownloadEntityId, out var nexusModsDownload))
            return;

        var fileMetadataId = nexusModsDownload.Value.FileMetadata.Id;
        var downloadInfo = activeDownloadsCache.Items
            .FirstOrDefault(d => d.FileMetadataId.Value == fileMetadataId);

        if (downloadInfo is not null)
            action(downloadsService, downloadInfo);
    }

    private static void OpenModPage(EntityId collectionDownloadEntityId, IConnection connection, IOSInterop osInterop)
    {
        var db = connection.Db;
        if (!CollectionDownloadNexusMods.TryGet(db, collectionDownloadEntityId, out var nexusModsDownload))
            return;

        var modPage = nexusModsDownload.Value.FileMetadata.ModPage;
        var uri = NexusModsUrlBuilder.GetModUri(modPage.GameDomain, modPage.Uid.ModId);
        osInterop.OpenUri(uri);
    }

    public BindableReactiveProperty<bool> IsInstalled { get; } = new(value: false);

    public BindableReactiveProperty<bool> HasInstalledAllOptionalItems { get; } = new(value: false);

    private readonly BehaviorSubject<bool> _canDownloadRequiredItems = new(initialValue: false);
    private readonly BehaviorSubject<bool> _canDownloadOptionalItems = new(initialValue: false);
    private readonly BehaviorSubject<bool> _isDownloadingRequiredItems = new(initialValue: false);
    private readonly BehaviorSubject<bool> _isDownloadingOptionalItems = new(initialValue: false);
    public BindableReactiveProperty<bool> IsDownloading { get; }

    private readonly BehaviorSubject<bool> _canInstallRequiredItems = new(initialValue: false);
    private readonly BehaviorSubject<bool> _canInstallOptionalItems = new(initialValue: false);
    public BindableReactiveProperty<bool> IsInstalling { get; } = new(value: false);
    private readonly BehaviorSubject<bool> _isRescanning = new(initialValue: false);

    public BindableReactiveProperty<bool> IsUpdateAvailable { get; }
    public BindableReactiveProperty<Optional<RevisionNumber>> NewestRevisionNumber { get; } = new();

    public string Name => _collection.Name;
    public string Summary => _collection.Summary.ValueOr(string.Empty);
    public ulong EndorsementCount => _collection.Endorsements.ValueOr(0ul);
    public ulong TotalDownloads => _collection.TotalDownloads.ValueOr(0ul);
    public string Category => _collection.Category.Name;
    public Size TotalSize => _revision.TotalSize.ValueOr(Size.Zero);
    public Percent OverallRating => Percent.Create(_revision.Collection.RecentRating.ValueOr(0), maximum: 100);

    public string AuthorName => _collection.Author.Name;
    public bool IsAdult => _revision.IsAdult.ValueOr(false);
    public CollectionSlug Slug => _collection.Slug;
    public RevisionNumber RevisionNumber => _revision.RevisionNumber;

    [Reactive] public IMarkdownRendererViewModel? InstructionsRenderer { get; set; }
    [Reactive] public ModInstructions[] RequiredModsInstructions { get; set; } = [];
    [Reactive] public ModInstructions[] OptionalModsInstructions { get; set; } = [];

    public int RequiredDownloadsCount { get; }
    public int OptionalDownloadsCount { get; }
    public string InstallRequiredItemsText { get; }
    [Reactive] public int CountDownloadedOptionalItems { get; private set; }
    [Reactive] public int CountDownloadedRequiredItems { get; private set; }

    [Reactive] public Bitmap? TileImage { get; private set; }
    [Reactive] public Bitmap? BackgroundImage { get; private set; }
    [Reactive] public Bitmap? AuthorAvatar { get; private set; }
    [Reactive] public string CollectionStatusText { get; private set; } = "";

    [Reactive] public bool CanDownloadAutomatically { get; private set; }

    public ReactiveCommand<NavigationInformation> CommandViewCollection { get; }
    public ReactiveCommand<Unit> CommandDownloadRequiredItems { get; }
    public ReactiveCommand<Unit> CommandInstallRequiredItems { get; }
    public ReactiveCommand<Unit> CommandDownloadOptionalItems { get; }
    public ReactiveCommand<Unit> CommandInstallOptionalItems { get; }
    public ReactiveCommand<Unit> CommandUpdateCollection { get; }
    public ReactiveCommand<Unit> CommandRescanDownloads { get; }

    public ReactiveCommand<Unit> CommandViewOnNexusMods { get; }
    public ReactiveCommand<Unit> CommandOpenJsonFile { get; }
    public ReactiveCommand<Unit> CommandDeleteAllDownloads { get; }
    public ReactiveCommand<Unit> CommandDeleteCollectionRevision { get; }

    public ModDetail[] ModList { get; }
    public ReactiveCommand<Unit> CommandCopyModList { get; }
}

public readonly record struct InstallMessage(CollectionDownloadEntity.ReadOnly DownloadEntity);

public readonly record struct DownloadNexusModsMessage(CollectionDownloadNexusMods.ReadOnly DownloadEntity);

public readonly record struct DownloadExternalMessage(CollectionDownloadExternal.ReadOnly DownloadEntity);

public readonly record struct ManualDownloadOpenModal(CollectionDownloadExternal.ReadOnly DownloadEntity);

public readonly record struct PauseDownloadMessage(EntityId EntityId);

public readonly record struct ResumeDownloadMessage(EntityId EntityId);

public readonly record struct CancelDownloadMessage(EntityId EntityId);

public readonly record struct ViewModPageMessage(EntityId EntityId);

public class CollectionDownloadTreeDataGridAdapter :
    TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>,
    ITreeDataGirdMessageAdapter<OneOf<InstallMessage, DownloadNexusModsMessage, DownloadExternalMessage, ManualDownloadOpenModal, PauseDownloadMessage, ResumeDownloadMessage, CancelDownloadMessage, ViewModPageMessage>>
{
    private readonly CollectionRevisionMetadata.ReadOnly _revisionMetadata;
    private readonly Optional<LoadoutId> _targetLoadout;
    private readonly ICollectionDataProvider _collectionDataProvider;

    public new R3.ReactiveProperty<CollectionDownloadsFilter> Filter { get; } = new(value: CollectionDownloadsFilter.OnlyRequired);

    public Subject<OneOf<InstallMessage, DownloadNexusModsMessage, DownloadExternalMessage, ManualDownloadOpenModal, PauseDownloadMessage, ResumeDownloadMessage, CancelDownloadMessage, ViewModPageMessage>> MessageSubject { get; } = new();

    public CollectionDownloadTreeDataGridAdapter(
        IServiceProvider serviceProvider,
        CollectionRevisionMetadata.ReadOnly revisionMetadata,
        Optional<LoadoutId> targetLoadout) : base(serviceProvider, new TreeDataGridSortingOptions
        {
            UseSortingStatePersistence = true,
            SettingsScopeKey = "CollectionDownloadTreeDataGrid",
            DefaultSortingState = new TreeDataGridSortingStateSettings
            {
                SortedColumnKey = CollectionColumns.DownloadProgress.ColumnTemplateResourceKey,
                SortDirection = ListSortDirection.Descending,
                SchemaRevision = 1,
            },
        })
    {
        _revisionMetadata = revisionMetadata;
        _targetLoadout = targetLoadout;
        _collectionDataProvider = serviceProvider.GetRequiredService<ICollectionDataProvider>();
    }

    protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical)
    {
        return _collectionDataProvider.ObserveCollectionItems(_revisionMetadata, Filter.AsSystemObservable(), _targetLoadout);
    }

    protected override void BeforeModelActivationHook(CompositeItemModel<EntityId> model)
    {
        base.BeforeModelActivationHook(model);

        model.SubscribeToComponentAndTrack<CollectionComponents.InstallAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.InstallComponentKey,
            state: this,
            factory: static (self, _, component) => component.CommandInstall.Subscribe(self, static (downloadEntity, self) => { self.MessageSubject.OnNext(new InstallMessage(downloadEntity)); })
        );

        model.SubscribeToComponentAndTrack<CollectionComponents.NexusModsDownloadAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.NexusModsDownloadComponentKey,
            state: this,
            factory: static (self, model, component) =>
            {
                var entityId = model.Key;
                var d1 = component.CommandDownload.Subscribe(self, static (downloadEntity, self) =>
                    self.MessageSubject.OnNext(new DownloadNexusModsMessage(downloadEntity)));
                var d2 = component.PauseCommand.Subscribe((self, entityId), static (_, state) =>
                    state.self.MessageSubject.OnNext(new PauseDownloadMessage(state.entityId)));
                var d3 = component.ResumeCommand.Subscribe((self, entityId), static (_, state) =>
                    state.self.MessageSubject.OnNext(new ResumeDownloadMessage(state.entityId)));
                var d4 = component.CancelCommand.Subscribe((self, entityId), static (_, state) =>
                    state.self.MessageSubject.OnNext(new CancelDownloadMessage(state.entityId)));
                return Disposable.Combine(d1, d2, d3, d4);
            }
        );

        model.SubscribeToComponentAndTrack<CollectionComponents.ExternalDownloadAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.ExternalDownloadComponentKey,
            state: this,
            factory: static (self, _, component) =>
                component.CommandDownload.Subscribe(self, static (downloadEntity, self) => { self.MessageSubject.OnNext(new DownloadExternalMessage(downloadEntity)); })
        );

        model.SubscribeToComponentAndTrack<CollectionComponents.ManualDownloadAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.ManualDownloadComponentKey,
            state: this,
            factory: static (self, _, component) =>
                component.CommandOpenModal.Subscribe(self, static (downloadEntity, self) => { self.MessageSubject.OnNext(new ManualDownloadOpenModal(downloadEntity)); })
        );

        model.SubscribeToComponentAndTrack<SharedComponents.ViewModPageAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.ViewModPageComponentKey,
            state: this,
            factory: static (self, model, component) =>
                component.CommandViewModPage.Subscribe((self, model.Key), static (_, state) =>
                    state.self.MessageSubject.OnNext(new ViewModPageMessage(state.Key)))
        );
    }

    /// <summary>
    /// Re-applies the current column sort to reflect changes in download status.
    /// </summary>
    public void ReapplyCurrentSort()
    {
        var source = Source.Value;
        if (source is null) return;

        foreach (var col in source.Columns)
        {
            if (col.SortDirection is { } direction)
            {
                source.SortBy(col, direction);
                return;
            }
        }
    }

    protected override IColumn<CompositeItemModel<EntityId>>[] CreateColumns(bool viewHierarchical)
    {
        var nameColumn = ColumnCreator.Create<EntityId, SharedColumns.Name>();

        return
        [
            viewHierarchical ? ITreeDataGridItemModel<CompositeItemModel<EntityId>, EntityId>.CreateExpanderColumn(nameColumn) : nameColumn,
            ColumnCreator.Create<EntityId, LibraryColumns.ItemVersion>(),
            ColumnCreator.Create<EntityId, SharedColumns.ItemSize>(),
            ColumnCreator.Create<EntityId, CollectionColumns.DownloadProgress>(sortDirection: ListSortDirection.Descending),
            ColumnCreator.Create<EntityId, CollectionColumns.DownloadSpeed>(),
            ColumnCreator.Create<EntityId, CollectionColumns.Actions>(),
        ];
    }

    private bool _isDisposed;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            MessageSubject.Dispose();
            _isDisposed = true;
        }

        base.Dispose(disposing);
    }
}
