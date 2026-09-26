using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NexusMods.Backend.FileExtractor;
using NexusMods.Backend.FileExtractor.Extractors;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Backend.Games;
using NexusMods.Backend.Games.Locators;
using NexusMods.Backend.Jobs;
using NexusMods.Backend.OS;
using NexusMods.Backend.Process;
using NexusMods.Backend.RuntimeDependency;
using NexusMods.FileExtractor;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Settings;
using NexusMods.UI.Sdk.Icons;
using NexusMods.UI.Sdk.Settings;

namespace NexusMods.Backend;

public static class ServiceExtensions
{
    public static IServiceCollection AddGameLocators(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<IGameLocator>(serviceProvider => new SteamLocator(serviceProvider.GetServices<IGameData>(), serviceProvider.GetRequiredService<ILoggerFactory>(), serviceProvider.GetRequiredService<IFileSystem>(), registry: null));

        return serviceCollection;
    }

    public static IServiceCollection AddGameServices(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddSingleton<IGameLocationsService, GameLocationsService>()
            .AddSingleton<IGameRegistry, GameRegistry>()
            .AddGameInstallMetadataModel()
            .AddSingleton<IGameLocator, ManuallyAddedLocator>()
            .AddManuallyAddedGameModel();
    }

    public static IServiceCollection AddOSInterop(this IServiceCollection serviceCollection, IOSInformation? os = null)
    {
        os ??= OSInformation.Shared;

        serviceCollection = serviceCollection
            .AddSingleton(os)
            .AddSingleton<IProcessRunner, ProcessRunner>();

        serviceCollection = serviceCollection.AddSingleton<IOSInterop, LinuxInterop>();

        return serviceCollection;
    }

    public static IServiceCollection AddRuntimeDependencies(this IServiceCollection serviceCollection, IOSInformation? os = null)
    {
        os ??= OSInformation.Shared;

        serviceCollection = serviceCollection.AddHostedService<RuntimeDependencyChecker>();

        if (os.IsLinux)
        {
            serviceCollection = serviceCollection
                .AddAllSingleton<IRuntimeDependency, XdgSettingsDependency>()
                .AddAllSingleton<IRuntimeDependency, UpdateDesktopDatabaseDependency>();
        }

        return serviceCollection;
    }

    public static IServiceCollection AddSettingsManager(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddSingleton<ISettingsManager, SettingsManager>()
            .AddStorageBackend<JsonStorageBackend>()
            .AddStorageBackend<MnemonicDBStorageBackend>(isDefault: true)
            .AddSingleton(new SectionDescriptor(
                Id: Sections.General,
                Name: "General",
                IconFunc: () => IconValues.Desktop,
                Priority: ushort.MaxValue
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.GameSpecific,
                Name: "Game specific",
                IconFunc: () => IconValues.Game,
                Priority: ushort.MinValue + 3
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.Advanced,
                Name: "Advanced",
                IconFunc: () => IconValues.School,
                Priority: ushort.MinValue + 2
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.DeveloperTools,
                Name: "Developer tools",
                IconFunc: () => IconValues.Code,
                Priority: ushort.MinValue + 1
            ))
            .AddSingleton(new SectionDescriptor(
                Id: Sections.Experimental,
                Name: "Experimental - Not currently supported",
                IconFunc: () => IconValues.WarningAmber,
                Priority: ushort.MinValue,
                Hidden: !ApplicationConstants.IsDebug
            ))
            .AddSettingModel();
    }
    
    /// <summary>
    /// Adds file extraction related services to the provided DI container.
    /// </summary>
    public static IServiceCollection AddFileExtractors(this IServiceCollection coll)
    {
        coll.AddSettings<FileExtractorSettings>();
        coll.AddFileExtractorVerbs();
        coll.AddSingleton<IFileExtractor, FileExtractor.FileExtractor>();
        coll.AddSingleton<IExtractor, SevenZipExtractor>();
        coll.AddSingleton<IExtractor, ManagedZipExtractor>();
        // Download naming detects the archive type by magic bytes when neither the name nor the URL has an extension
        coll.AddSingleton<ISignatureCheckerFactory, NexusMods.Backend.FileExtractor.FileSignatures.SignatureCheckerFactory>();
        coll.TryAddSingleton<TemporaryFileManager, TemporaryFileManagerEx>();
        return coll;
    }
    
    public static IServiceCollection AddJobMonitor(this IServiceCollection serviceCollection)
    {
        return serviceCollection
            .AddSingleton<IJobMonitor, JobMonitor>();
    }
}
