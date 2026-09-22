using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Serialization;
using NexusMods.Backend;
using NexusMods.CrossPlatform;
using NexusMods.DataModel;
using NexusMods.FileExtractor;
using NexusMods.Games.FileHashes;
using NexusMods.Library;
using NexusMods.Networking.HttpDownloader;
using NexusMods.Networking.HttpDownloader.Tests;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using NexusMods.SingleProcess;
using NexusMods.StandardGameLocators;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit.DependencyInjection.Logging;
using NexusMods.Sdk.FileExtractor;

namespace NexusMods.CLI.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"NexusMods.UI.Tests.Tests-{Guid.NewGuid()}";

        services
                .AddSingleton<CommandLineConfigurator>()
                .AddFileSystem()
                .AddSettingsManager()
                .AddDataModel()
                .AddLibrary()
                .AddLibraryModels()
                .AddJobMonitor()
                .OverrideSettingsForTests<DataModelSettings>(settings => settings with
                {
                    UseInMemoryDataModel = true,
                    MnemonicDBPath = new ConfigurablePath(baseKnownPath, $"{baseDirectory}/MnemonicDB.rocksdb"),
                    ArchiveLocations = [
                        new ConfigurablePath(baseKnownPath, $"{baseDirectory}/Archives"),
                    ],
                })
                .AddFileExtractors()
                // Keep the temp folder out of the user's real XDG_STATE_HOME: TemporaryFileManager deletes it on dispose
                .OverrideSettingsForTests<FileExtractorSettings>(settings => settings with
                {
                    TempFolderLocation = new ConfigurablePath(KnownPath.EntryDirectory, $"Temp-{Guid.NewGuid()}"),
                })
                .AddFileHashes()
                .AddCLI()
                .AddHttpDownloader()
                .AddNexusWebApi(true)
                .AddLoadoutAbstractions()
                .AddSerializationAbstractions()
                .AddGames()
                .AddGameServices()
                .AddOSInterop()
                .AddRuntimeDependencies()
                .AddSettings<LoggingSettings>()
                .AddLogging(builder => builder.AddXunitOutput().SetMinimumLevel(LogLevel.Trace))
                .AddSingleton<LocalHttpServer>()
                .AddLogging(builder => builder.AddXUnit())
                .Validate();
    }
}

