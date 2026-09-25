using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls.GameWidget;
using NexusMods.App.UI.Pages.MyGames.WinePrefix;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.UI.Sdk.Icons;
using NexusMods.MnemonicDB.Abstractions;
using OneOf;
using OneOf.Types;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData.Aggregation;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Sdk.Settings;
using NexusMods.App.UI.Controls.MarkdownRenderer;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Overlays.Generic.MessageBox.OkCancel;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.App.UI.Settings;
using NexusMods.Collections;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;
using GameInstallMetadata = NexusMods.Sdk.Games.GameInstallMetadata;
using NexusMods.Abstractions.Games.FileHashes;

namespace NexusMods.App.UI.Pages.MyGames;

[UsedImplicitly]
public class MyGamesViewModel : APageViewModel<IMyGamesViewModel>, IMyGamesViewModel
{
    private const string TrelloPublicRoadmapUrl = "https://trello.com/b/gPzMuIr3/nexus-mods-app-roadmap";

    private readonly ILibraryService _libraryService;
    private readonly CollectionDownloader _collectionDownloader;
    private readonly IWindowManager _windowManager;
    private readonly IJobMonitor _jobMonitor;
    private readonly IOverlayController _overlayController;
    private readonly IConnection _connection;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISynchronizerService _syncService;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IFileHashesService _fileHashesService;
    private readonly IGameRegistry _gameRegistry;
    private readonly IToolManager _toolManager;
    private readonly IWindowNotificationService _notificationService;
    private readonly ILogger<MyGamesViewModel> _logger;
    private readonly BehaviorSubject<Unit> _refreshSignal = new(Unit.Default);
    private readonly SourceList<GameInstallation> _sourceList = new();

    private ReadOnlyObservableCollection<IGameWidgetViewModel> _installedGames = new([]);

    public ReactiveCommand<Unit, Unit> OpenRoadmapCommand { get; }
    public ReactiveCommand<Unit, Unit> AddGameManuallyCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshGamesCommand { get; }
    public ReadOnlyObservableCollection<IGameWidgetViewModel> InstalledGames => _installedGames;
    public IWinePrefixStatusViewModel? WinePrefixStatus { get; private set; }

    public MyGamesViewModel(
        IWindowManager windowManager,
        IServiceProvider serviceProvider,
        IConnection conn,
        ILogger<MyGamesViewModel> logger,
        IOverlayController overlayController,
        IOSInterop osInterop,
        ISynchronizerService syncService,
        IGameRegistry gameRegistry,
        IToolManager toolManager) : base(windowManager)
    {
        var settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();
        var experimentalSettings = settingsManager.Get<ExperimentalSettings>();

        var libraryDataProviders = serviceProvider.GetServices<ILibraryDataProvider>().ToArray();

        _collectionDownloader = serviceProvider.GetRequiredService<CollectionDownloader>();
        _libraryService = serviceProvider.GetRequiredService<ILibraryService>();
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _overlayController = overlayController;
        _connection = conn;
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _fileHashesService = serviceProvider.GetRequiredService<IFileHashesService>();
        _gameRegistry = gameRegistry;
        _toolManager = toolManager;
        _logger = logger;
        _notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();

        TabTitle = Language.MyGames;
        TabIcon = IconValues.GamepadOutline;

        _serviceProvider = serviceProvider;
        _syncService = syncService;
        _windowManager = windowManager;

        OpenRoadmapCommand = ReactiveCommand.Create(() =>
        {
            var uri = new Uri(TrelloPublicRoadmapUrl);
            osInterop.OpenUri(uri);
        });

        RefreshGamesCommand = ReactiveCommand.Create(() =>
        {
            _gameRegistry.ClearCache();
            _refreshSignal.OnNext(Unit.Default);
        });

        AddGameManuallyCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var overlay = new ManualAddGameOverlayViewModel(_serviceProvider.GetRequiredService<IAvaloniaInterop>());
            var result = await _overlayController.EnqueueAndWait(overlay);
            if (result is null || !result.Confirmed) return;

            var gamePath = _serviceProvider.GetRequiredService<IFileSystem>().FromUnsanitizedFullPath(result.GamePath);
            var exePath = gamePath.Combine("bin/x64/Cyberpunk2077.exe");

            if (!exePath.FileExists)
            {
                var messageBox = new MessageBoxOkViewModel
                {
                    Title = "Invalid Game Path",
                    Description = "The selected folder does not appear to contain a Cyberpunk 2077 installation (bin/x64/Cyberpunk2077.exe not found).",
                    MarkdownRenderer = null
                };
                await _overlayController.EnqueueAndWait(messageBox);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.WinePrefix))
            {
                var winePrefixPath = _serviceProvider.GetRequiredService<IFileSystem>().FromUnsanitizedFullPath(result.WinePrefix);
                if (!winePrefixPath.Combine("user.reg").FileExists)
                {
                    var messageBox = new MessageBoxOkViewModel
                    {
                        Title = "Invalid WINE Prefix",
                        Description = "The selected folder does not appear to be a valid WINE prefix (user.reg not found).",
                        MarkdownRenderer = null
                    };
                    await _overlayController.EnqueueAndWait(messageBox);
                    return;
                }
            }

            // Check for duplicates
            var existing = ManuallyAddedGame.All(conn.Db)
                .Any(x => string.Equals(x.Path, result.GamePath, StringComparison.OrdinalIgnoreCase));
            if (existing)
            {
                var messageBox = new MessageBoxOkViewModel
                {
                    Title = "Game Already Added",
                    Description = "This game path has already been added manually.",
                    MarkdownRenderer = null
                };
                await _overlayController.EnqueueAndWait(messageBox);
                return;
            }

            using var tx = conn.BeginTransaction();
            _ = new ManuallyAddedGame.New(tx)
            {
                GameId = NexusModsGameId.From(3333),
                Version = "Manual",
                Path = result.GamePath,
                WinePrefix = result.WinePrefix,
            };
            await tx.Commit();

            // Forzar re-deteccion
            _gameRegistry.ClearCache();
            _refreshSignal.OnNext(Unit.Default);
            
            // Wait for refresh to propagate? 
            // We can manually locate it here just to get the object for ManageGame
            var installations = gameRegistry.LocateGameInstallations();
            var cp2077 = installations.FirstOrDefault(i => i.Game.GameId == GameId.From("RedEngine.Cyberpunk2077") && i.LocatorResult.Store == GameStore.ManuallyAdded);
            if (cp2077 is null) return;

            // Crear loadout y navegar a Library
            await Task.Run(async () => await ManageGame(cp2077));
            NavigateToLoadoutLibrary(conn, cp2077);
        });

        this.WhenActivated(d =>
            {
                _refreshSignal
                    .Subscribe(_ =>
                    {
                        var games = gameRegistry.LocateGameInstallations()
                            .Where(game =>
                            {
                                if (experimentalSettings.EnableAllGames) return true;
                                return experimentalSettings.SupportedGames.Contains(game.Game.GameId);
                            });
                        
                        _sourceList.Edit(innerList =>
                        {
                            innerList.Clear();
                            innerList.AddRange(games);
                        });
                    })
                    .DisposeWith(d);

                _sourceList.Connect()
                    .Transform(installation =>
                        {
                            var vm = _serviceProvider.GetRequiredService<IGameWidgetViewModel>();
                            vm.Installation = installation;

                            vm.AddGameCommand = ReactiveCommand.CreateFromTask(async () =>
                            {
                                try
                                {
                                    await AddGameHandler(installation, vm);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error adding game");
                                    vm.State = GameWidgetState.DetectedGame;
                                }
                            });

                            vm.DeepCleanCommand = ReactiveCommand.CreateFromTask(async () =>
                            {
                                try
                                {
                                    var gamePath = installation.Locations[LocationId.Game].Path;
                                    var dialog = DialogFactory.CreateStandardDialog(
                                        title: "Deep Clean Cyberpunk 2077",
                                        new StandardDialogParameters()
                                        {
                                            Text = $"This will move all non-original mod folders to a backup directory.\n\nTarget Path: {gamePath}\n\nDo you want to continue?",
                                        },
                                        buttonDefinitions:
                                        [
                                            DialogStandardButtons.Cancel,
                                            new DialogButtonDefinition("Deep Clean", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
                                        ]
                                    );

                                    var dialogResult = await _windowManager.ShowDialog(dialog, DialogWindowType.Modal);
                                    if (dialogResult.ButtonId != ButtonDefinitionId.Accept) return;

                                    vm.State = GameWidgetState.AddingGame;
                                    
                                    // Get or create marker loadout
                                    var loadoutId = GetLoadout(conn, installation);
                                    Loadout.ReadOnly loadout;
                                    if (loadoutId.HasValue)
                                    {
                                        loadout = Loadout.Load(conn.Db, loadoutId.Value);
                                    }
                                    else
                                    {
                                        loadout = await _loadoutManager.CreateLoadout(installation);
                                    }

                                    var tool = _toolManager.GetTools(loadout).OfType<CyberpunkDeepCleanTool>().FirstOrDefault();
                                    if (tool is not null)
                                    {
                                        await tool.Execute(loadout, CancellationToken.None);
                                        _notificationService.ShowToast("Deep clean completed. Non-game files moved to backup.", ToastNotificationVariant.Success);
                                    }
                                    else
                                    {
                                        _notificationService.ShowToast("Deep clean tool not found.", ToastNotificationVariant.Failure);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error during deep clean");
                                }
                                finally
                                {
                                    vm.State = GameWidgetState.DetectedGame;
                                    _refreshSignal.OnNext(Unit.Default);
                                }
                            });

                            vm.RemoveAllLoadoutsCommand = ReactiveCommand.CreateFromTask(async () =>
                            {
                                try
                                {
                                    if (GetJobRunningForGameInstallation(installation).IsT2) return;

                                    var filesToDelete = libraryDataProviders.SelectMany(dataProvider => dataProvider.GetAllFiles(installation.Game.GameId)).ToArray();
                                    var totalSize = filesToDelete.Sum(static Size (file) => file.Size);

                                    var collections = CollectionDownloader.GetCollections(conn.Db, installation.Game.NexusModsGameId.Value);

                                    var overlay = new RemoveGameOverlayViewModel
                                    {
                                        GameName = installation.Game.DisplayName,
                                        NumDownloads = filesToDelete.Length,
                                        SumDownloadsSize = totalSize,
                                        NumCollections = collections.Length,
                                    };

                                    var result = await overlayController.EnqueueAndWait(overlay);
                                    if (!result.ShouldRemoveGame) return;

                                    vm.State = GameWidgetState.RemovingGame;
                                    await Task.Run(async () => await RemoveGame(installation, shouldDeleteDownloads: result.ShouldDeleteDownloads, shouldCleanGameFolder: result.ShouldCleanGameFolder, filesToDelete, collections));
                                    _gameRegistry.ClearCache();
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error removing game");
                                }
                                finally
                                {
                                    vm.State = GameWidgetState.DetectedGame;
                                    _refreshSignal.OnNext(Unit.Default);
                                }
                            });

                            vm.ViewGameCommand = ReactiveCommand.Create(() =>
                            {
                                NavigateToLoadoutLibrary(conn, installation);
                            });

                            vm.DismissCommand = ReactiveCommand.CreateFromTask(async () =>
                            {
                                try
                                {
                                    using var tx = conn.BeginTransaction();
                                    var removed = false;

                                    // For manually-added games: remove the DB entry so they won't re-appear
                                    if (installation.LocatorResult.Store == GameStore.ManuallyAdded)
                                    {
                                        var pathStr = installation.Locations[LocationId.Game].Path.ToString();
                                        foreach (var entry in ManuallyAddedGame.All(conn.Db).Where(x => string.Equals(x.Path, pathStr, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            tx.Delete(entry.Id, recursive: true);
                                            removed = true;
                                        }
                                    }

                                    // Remove metadata (orphaned or not)
                                    if (_gameRegistry.TryGetMetadata(installation, out var meta))
                                    {
                                        tx.Delete(meta.Id, recursive: true);
                                        removed = true;
                                    }

                                    if (removed) await tx.Commit();
                                    _gameRegistry.ClearCache();
                                    _refreshSignal.OnNext(Unit.Default);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error dismissing installation");
                                }
                            });

                            vm.IsManagedObservable = Loadout.ObserveAll(conn)
                                .Filter(l => l.IsVisible() && 
                                             l.InstallationInstance.Game.GameId == installation.Game.GameId && 
                                             l.InstallationInstance.LocatorResult.Store == installation.LocatorResult.Store &&
                                             string.Equals(l.InstallationInstance.Locations[LocationId.Game].Path.ToString(), installation.Locations[LocationId.Game].Path.ToString(), StringComparison.OrdinalIgnoreCase))
                                .Count()
                                .Select(c => c > 0);

                            var job = GetJobRunningForGameInstallation(installation);

                            // fixes when the page loads and a job is still running
                            vm.State = job.Value switch
                            {
                                CreateLoadoutJob _ => GameWidgetState.AddingGame,
                                UnmanageGameJob _ => GameWidgetState.RemovingGame,
                                _ => GameWidgetState.DetectedGame,
                            };

                            return vm;
                        }
                    )
                    .OnUI()
                    .Bind(out _installedGames)
                    .SubscribeWithErrorLogging()
                    .DisposeWith(d);

                // Create Wine prefix status panel for the first installed game
                // We observe the source list directly to update this
                _sourceList.Connect()
                    .ToCollection()
                    .Select(list => list.FirstOrDefault())
                    .Subscribe(firstInstallation =>
                    {
                        if (firstInstallation is not null)
                        {
                            var runtimeDeps = serviceProvider.GetServices<IRuntimeDependency>();
                            WinePrefixStatus = new WinePrefixStatusViewModel(firstInstallation, runtimeDeps);
                            // We need to notify property changed for WinePrefixStatus but since it's a property without INPC support (simple property),
                            // and the view binds to it via viewmodel, we might need RaisePropertyChanged.
                            // But MyGamesViewModel is an APageViewModel which inherits ReactiveObject.
                            this.RaisePropertyChanged(nameof(WinePrefixStatus));
                        }
                        else
                        {
                            WinePrefixStatus = null;
                            this.RaisePropertyChanged(nameof(WinePrefixStatus));
                        }
                    })
                    .DisposeWith(d);
            }
        );
    }

    private OneOf<None, CreateLoadoutJob, UnmanageGameJob> GetJobRunningForGameInstallation(GameInstallation installation)
    {
        foreach (var job in _jobMonitor.Jobs)
        {
            if (job.Status != JobStatus.Running) continue;

            if (job.Definition is CreateLoadoutJob createLoadoutJob && createLoadoutJob.Installation.Equals(installation)) return createLoadoutJob;
            if (job.Definition is UnmanageGameJob unmanageGameJob && unmanageGameJob.Installation.Equals(installation)) return unmanageGameJob;
        }

        return OneOf<None, CreateLoadoutJob, UnmanageGameJob>.FromT0(new None());
    }

    private async Task RemoveGame(GameInstallation installation, bool shouldDeleteDownloads, bool shouldCleanGameFolder, LibraryFile.ReadOnly[] filesToDelete, CollectionMetadata.ReadOnly[] collections)
    {
        _logger.LogInformation("Removing game management for {Game} at {Path}", installation.Game.DisplayName, installation.Locations[LocationId.Game].Path);

        // 1. Try to unmanage files if it has metadata and cleaning is requested
        if (shouldCleanGameFolder && _gameRegistry.TryGetMetadata(installation, out _))
        {
            try 
            {
                _logger.LogInformation("Attempting to unmanage game files (CleanFolder=true)");
                await _syncService.UnManage(installation, cleanGameFolder: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UnManage file operation failed. Continuing with database removal.");
            }
        }

        // 2. Aggressive database cleanup
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();
        var removedAny = false;

        // Remove Manual entry
        if (installation.LocatorResult.Store == GameStore.ManuallyAdded)
        {
            if (ulong.TryParse(installation.LocatorResult.StoreIdentifier, out var entityIdValue))
            {
                var entityId = EntityId.From(entityIdValue);
                if (ManuallyAddedGame.Load(db, entityId).IsValid())
                {
                    _logger.LogInformation("Deleting ManuallyAddedGame entry: {Id}", entityId);
                    tx.Delete(entityId, recursive: true);
                    removedAny = true;
                }
            }
            
            // Path-based cleanup fallback
            var pathStr = installation.Locations[LocationId.Game].Path.ToString();
            foreach (var entry in ManuallyAddedGame.All(db).Where(x => string.Equals(x.Path, pathStr, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Deleting duplicate ManuallyAddedGame entry by path: {Id}", entry.Id);
                tx.Delete(entry.Id, recursive: true);
                removedAny = true;
            }
        }

        // Remove Metadata if it still exists
        if (_gameRegistry.TryGetMetadata(installation, out var metadata))
        {
            _logger.LogInformation("Deleting GameInstallMetadata: {Id}", metadata.Id);
            tx.Delete(metadata.Id, recursive: true);
            removedAny = true;
        }

        if (removedAny) await tx.Commit();

        if (!shouldDeleteDownloads) return;
        await _libraryService.RemoveLibraryItems(filesToDelete.Select(file => file.AsLibraryItem()));

        foreach (var collection in collections)
        {
            await _collectionDownloader.DeleteCollection(collection);
        }
    }
    
    private async Task AddGameHandler(GameInstallation installation, IGameWidgetViewModel vm)
    {
        if (GetJobRunningForGameInstallation(installation).IsT1) return;

        // Check if game is actually installed (exists on disk)
        var primaryFile = installation.Locations.ToAbsolutePath(installation.GetGame().GetPrimaryFile(installation));
        if (!primaryFile.FileExists)
        {
            var messageBox = new MessageBoxOkViewModel
            {
                Title = "Game Not Found",
                Description = $"The game folder for {installation.Game.DisplayName} was found, but the main executable is missing ({primaryFile.FileName} not found). Please make sure the game is fully installed.",
                MarkdownRenderer = null
            };
            await _overlayController.EnqueueAndWait(messageBox);
            return;
        }

        vm.State = GameWidgetState.AddingGame;
        var loadout = await Task.Run(async () => await ManageGame(installation));
        
        // Check if there are external changes
        var changeEntries = await GetExternalChangesItems(loadout);
        vm.State = GameWidgetState.ManagedGame;
        
        // Offer to clean them up, only when the vanilla file list is known: without it the "external changes" are
        // the original game files, and cleaning them deletes the game
        if (changeEntries.Length > 0 && HasVanillaData(installation))
        {
            var (revert, doNothing, clean) = (ButtonDefinitionId.Cancel, ButtonDefinitionId.From("doNothing"), ButtonDefinitionId.Accept);
            var result = await ShowCleanGameFolderDialog(revert,
                doNothing,
                clean,
                changeEntries,
                installation
            );
            
            if (result == revert)
            {
                // Revert the loadout creation
                vm.State = GameWidgetState.RemovingGame;
                await Task.Run(async () => await _syncService.UnManage(installation, cleanGameFolder: false));
                vm.State = GameWidgetState.DetectedGame;
                return;
            }
            if (result == clean)
            {
                vm.State = GameWidgetState.AddingGame;
                await CleanGameFolder(installation, loadout);
                vm.State = GameWidgetState.ManagedGame;
            }
        }
        
        NavigateToLoadoutLibrary(_connection, installation);
    }
    
    private ValueTask<LoadoutItemWithTargetPath.ReadOnly[]> GetExternalChangesItems(Loadout.ReadOnly loadout)
    {
        var db = _connection.Db;
        if (!LoadoutOverridesGroup.FindByOverridesFor(db, loadout.Id).TryGetFirst(out var overrideGroup))
            return ValueTask.FromResult<LoadoutItemWithTargetPath.ReadOnly[]>([]);

        return ValueTask.FromResult(overrideGroup.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath().ToArray());
    }
    
    private async Task<ButtonDefinitionId> ShowCleanGameFolderDialog(
        ButtonDefinitionId revert,
        ButtonDefinitionId doNothing,
        ButtonDefinitionId clean,
        LoadoutItemWithTargetPath.ReadOnly[] changeEntries,
        GameInstallation installation)
    {
        var markdownVm = _serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdownVm.Contents = $"""
            We found {changeEntries.Length} files in the game folder that aren’t part of a clean install. To avoid conflicts with mods, **we recommend starting with a clean folder**.
            
            #### Important
            - **Your original game files will NEVER be deleted.**
            - "Cleaning" only moves or removes files that were added by mods or other external tools.
            
            If you keep existing files (not recommended):
            - The existing files will be placed in **External Changes**.
            - Files in External Changes **override any mods you install later**.
            """;
        
        var dialog = DialogFactory.CreateStandardDialog(
            title: $"Your {installation.Game.DisplayName} folder isn't a clean install",
            new StandardDialogParameters()
            {
                Markdown = markdownVm,
            },
            buttonDefinitions:
            [
                new DialogButtonDefinition("Cancel", revert),
                new DialogButtonDefinition("Keep existing files", doNothing, ButtonAction.Reject),
                new DialogButtonDefinition("Clean folder", clean, ButtonAction.Accept, ButtonStyling.Primary),
            ]
        );
        
        return (await _windowManager.ShowDialog(dialog, DialogWindowType.Modal)).ButtonId;
    }
    
    private bool HasVanillaData(GameInstallation installation) =>
        _fileHashesService.UnknownLocatorIds(installation.LocatorResult.Store, installation.LocatorResult.LocatorIds.Distinct().ToArray()).Length == 0;

    private async Task CleanGameFolder(GameInstallation installation, Loadout.ReadOnly loadout)
    {
        if (!HasVanillaData(installation))
            throw new InvalidOperationException("No hay lista de archivos originales para esta versión del juego; limpiar la carpeta borraría el juego");

        var db = _connection.Db;
        var tx = _connection.BeginTransaction();
        var changeEntries = await GetExternalChangesItems(loadout.Rebase()); 
        
        // Remove items from External Changes mod
        foreach (var entry in changeEntries)
        {
            tx.Delete(entry.Id, recursive: false);
        }
        await tx.Commit();

        loadout = loadout.Rebase();
        var game = installation.GetGame();
        var syncer = game.Synchronizer;
        
        // Apply clean state to game folder
        await syncer.Synchronize(Loadout.Load(db, loadout));
    }

    private async Task<Loadout.ReadOnly> ManageGame(GameInstallation installation)
    {
        return await _loadoutManager.CreateLoadout(installation);
    }

    private Optional<LoadoutId> GetLoadout(IConnection conn, GameInstallation installation)
    {
        if (!_gameRegistry.TryGetMetadata(installation, out var metadata)) return Optional<LoadoutId>.None;
        if (metadata.Contains(GameInstallMetadata.LastSyncedLoadout))
        {
            return metadata.LastSyncedLoadout.LoadoutId;
        }

        // no applied loadout, return the first one
        var loadout = Loadout.All(conn.Db).FirstOrOptional(loadout => loadout.IsVisible() && loadout.InstallationInstance.Equals(installation));
        return loadout.HasValue ? loadout.Value.LoadoutId : Optional<LoadoutId>.None;
    }
    
    private void NavigateToLoadoutLibrary(IConnection conn, GameInstallation installation)
    {
        var fistLoadout = GetLoadout(conn, installation);
        if (!fistLoadout.HasValue) return;
        var loadoutId = fistLoadout.Value;
        Dispatcher.UIThread.Invoke(() =>
            {
                var workspaceController = _windowManager.ActiveWorkspaceController;
                
                workspaceController.ChangeOrCreateWorkspaceByContext(
                    context => context.LoadoutId == loadoutId,
                    () => new PageData
                    {
                        FactoryId = LibraryPageFactory.StaticId,
                        Context = new LibraryPageContext()
                        {
                            LoadoutId = loadoutId,
                        },
                    },
                    () => new LoadoutContext
                    {
                        LoadoutId = loadoutId,
                    }
                );
            }
        );
    }
}
