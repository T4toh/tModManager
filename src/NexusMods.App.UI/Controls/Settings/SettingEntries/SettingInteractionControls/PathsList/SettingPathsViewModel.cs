using System.Reactive.Disposables;
using Avalonia.Platform.Storage;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Settings;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ConfigurablePathsContainer = NexusMods.UI.Sdk.Settings.ConfigurablePathsContainer;
using Disposable = R3.Disposable;
using ISettingsManager = NexusMods.Sdk.Settings.ISettingsManager;
using ReactiveCommand = R3.ReactiveCommand;

namespace NexusMods.App.UI.Controls.Settings.SettingEntries.PathsList;

public class SettingPathsViewModel : AViewModel<ISettingPathsViewModel>, ISettingPathsViewModel
{
    public IPropertyValueContainer ValueContainer => ConfigurablePathsContainer;
    public ConfigurablePathsContainer ConfigurablePathsContainer { get; }
    [Reactive] public bool HasChanged { get; private set; }
    [Reactive] public string? ErrorMessage { get; private set; }

    public IStorageProvider? StorageProvider { get; set; }
    public ReactiveCommand CommandChangeLocation { get; }

    public SettingPathsViewModel(IOSInterop osInterop, ConfigurablePathsContainer pathsContainer, Func<object, ValidationResult>? validation = null)
    {
        ConfigurablePathsContainer = pathsContainer;

        CommandChangeLocation = new ReactiveCommand(
            executeAsync: async (_, cancellationToken) =>
            {
                var paths = await StorageProvider!.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = "Select a folder",
                });

                if (paths.Count != 1) return;
                var localPath = paths[0].TryGetLocalPath();
                if (localPath is null) return;

                ConfigurablePath[] picked = [new ConfigurablePath(null, localPath)];
                var result = validation?.Invoke(picked) ?? ValidationResult.CreateSuccessful();
                if (result.IsFailed())
                {
                    ErrorMessage = result.Value.AsT0.Reason;
                    return;
                }

                ErrorMessage = null;
                ConfigurablePathsContainer.CurrentValue = picked;
            }, awaitOperation: AwaitOperation.Drop, configureAwait: false, maxSequential: 1
        );

        this.WhenActivated(disposables =>
        {
            Disposable.Create(this, static vm => vm.StorageProvider = null).AddTo(disposables);

            ValueContainer.WhenAnyValue(x => x.HasChanged)
                .BindToVM(this, vm => vm.HasChanged)
                .DisposeWith(disposables);
        });
    }
}

[UsedImplicitly]
public class SettingPathsFactory : IInteractionControlFactory<ConfigurablePathsContainerOption>
{
    public IInteractionControl Create(
        IServiceProvider serviceProvider,
        ISettingsManager settingsManager,
        ConfigurablePathsContainerOption containerOptions,
        PropertyConfig propertyConfig)
    {
        return new SettingPathsViewModel(
             osInterop: serviceProvider.GetRequiredService<IOSInterop>(),
             pathsContainer: new ConfigurablePathsContainer(
                 value: propertyConfig.GetValueCasted<ConfigurablePath[]>(settingsManager),
                 defaultValue: propertyConfig.GetDefaultValueCasted<ConfigurablePath[]>(settingsManager),
                 config: propertyConfig
             ),
             validation: propertyConfig.Options.Validation
        );
    }
}

