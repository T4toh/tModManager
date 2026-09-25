using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.FileExtractor;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.CrossPlatform;
using NexusMods.DataModel;
using NexusMods.DataModel.Storage;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Sdk.IO;

namespace NexusMods.App.Commandline;

/// <summary>
///     These verbs are used in cleaning up unneeded data related to the application.
///     Namely, Garbage Collection tasks.
/// </summary>
internal static class CleanupVerbs
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    internal static IServiceCollection AddCleanupVerbs(this IServiceCollection services) =>
        services.AddVerb(() => UninstallApp);
    
    [Verb("uninstall-app", "Uninstall the application and revert games to their original state")]
    private static async Task<int> UninstallApp(
        [Injected] IRenderer renderer,
        [Injected] IConnection conn,
        [Injected] ISettingsManager settingsManager,
        [Injected] IFileSystem fileSystem,
        [Injected] ISynchronizerService syncService,
        [Injected] IStorageAnalyzer storageAnalyzer)
    {
        // Step 1: Revert the managed games to their original state
        var db = conn.Db;
        var managedInstallations = Loadout.All(db)
            .Select(loadout => loadout.InstallationInstance)
            .Distinct();

        var revertFailed = false;
        foreach (var installation in managedInstallations)
        {
            try
            {
                await syncService.UnManage(installation, false);
                await renderer.Text($"Reverted {installation.Game.DisplayName} to its original state");
            }
            catch (Exception ex)
            {
                revertFailed = true;
                await renderer.Error(ex, "Error reverting {0}: {1}", installation.Game.DisplayName, ex.Message);
            }
        }

        // Deleting the store and the DB now would lose the backups of the original game files the mods overwrote
        if (revertFailed)
        {
            await renderer.Text("A game could not be reverted; nothing was deleted. Fix the error above and run uninstall-app again.");
            return -1;
        }

        // Step 2: Delete application-specific directories
        try
        {
            /*
                Note (Sewer): There's a possibility some data may be left behind if
                the user manually modified the logging configuration to point
                their logs outside of the regular 'Logs' folder.

                Specifically, the historical logs may use Layout Renderers
                https://nlog-project.org/config/?tab=layout-renderers that
                are other than {##}. Resolving these on our end would be hard.
            */
            var dataModelSettings = settingsManager.Get<DataModelSettings>();
            var fileExtractorSettings = settingsManager.Get<FileExtractorSettings>();
            var loggingSettings = settingsManager.Get<LoggingSettings>();
            var appFiles = (AbsolutePath[])
            [
                loggingSettings.MainProcessLogFilePath.ToPath(fileSystem),
                loggingSettings.SlimProcessLogFilePath.ToPath(fileSystem),
            ];

            // The storage location is a folder the user picked (it can be ~/Mods or a whole disk): only the files the
            // store owns are deleted, and the folder itself only if that leaves it empty
            await storageAnalyzer.DeleteArchivesAsync();
            foreach (var archiveRoot in dataModelSettings.ArchiveLocations.Select(path => path.ToPath(fileSystem)))
            {
                if (archiveRoot.DirectoryExists() && !archiveRoot.EnumerateFiles("*", recursive: false).Any() && !archiveRoot.EnumerateDirectories(recursive: false).Any())
                    archiveRoot.DeleteDirectory(recursive: false);
            }

            // The DB path comes from a settings file too: delete it only when it really is a database
            var dbPath = dataModelSettings.MnemonicDBPath.ToPath(fileSystem);
            if (dbPath.DirectoryExists() && !DataModelSettings.LooksLikeRocksDb(dbPath))
                await renderer.Text("Skipping {0}: it doesn't look like the app's database", dbPath);

            // Backups (Deep Clean snapshots, possibly the only copy of mod files moved out of the game) and
            // Downloads (the original archives) are the user's data: keep them
            var dataDirectory = DataModelSettings.GetLocalApplicationDataDirectory(fileSystem);
            string[] kept = ["Backups", "Downloads"];
            var dataDirectoryEntries = dataDirectory.DirectoryExists()
                ? dataDirectory.EnumerateDirectories(recursive: false).Where(dir => !kept.Contains(dir.FileName.ToString()))
                : [];

            var appDirectories = new[]
                {
                    fileExtractorSettings.TempFolderLocation.ToPath(fileSystem),
                    LoggingSettings.GetLogBaseFolder(OSInformation.Shared, fileSystem),
                }
                .Concat(DataModelSettings.LooksLikeRocksDb(dbPath) ? [dbPath] : [])
                .Concat(dataDirectoryEntries);

            if (fileSystem.OS.IsUnix())
                await DeleteRemainingFilesUnix(renderer, appFiles, appDirectories);
            else
                await DeleteRemainingFilesWindows(appFiles, appDirectories);

            await renderer.Text("Application uninstalled successfully. Kept your backups and downloads in {0}", dataDirectory);
            return 0;
        }
        catch (Exception ex)
        {
            await renderer.Error(ex, $"Error deleting application directories: {ex.Message}");
            return -1;
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static async Task DeleteRemainingFilesUnix(IRenderer renderer, AbsolutePath[] appFiles, IEnumerable<AbsolutePath> appDirectories)
    {
        await DeleteFilesUnix(renderer, appFiles);
        await DeleteDirectoriesUnix(renderer, appDirectories);
    }
    
    private static async Task DeleteRemainingFilesWindows(AbsolutePath[] appFiles, IEnumerable<AbsolutePath> appDirectories)
    {
        var filesToDeletePath = Path.Combine(Path.GetTempPath(), "files_to_delete.txt");
        await File.WriteAllLinesAsync(filesToDeletePath, appFiles.Select(f => f.ToString()));

        var directoriesToDeletePath = Path.Combine(Path.GetTempPath(), "directories_to_delete.txt");
        await File.WriteAllLinesAsync(directoriesToDeletePath, appDirectories.Select(d => d.ToString()));

        // uninstall-helper.ps1 is beside our current EXE.
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "uninstall-helper.ps1");

        // Execute the PowerShell script
        var args = $"-ExecutionPolicy Bypass -Command \"& \'{scriptPath}\' -FilesToDeletePath \'{filesToDeletePath}\' -DirectoriesToDeletePath \'{directoriesToDeletePath}\'\"";
        await Cli.Wrap("powershell")
            .WithArguments(args)
            .ExecuteAsync();

        // Clean up the temporary files
        // Note: This should never be executed in practice.
        File.Delete(filesToDeletePath);
        File.Delete(directoriesToDeletePath);
    }

    private static async Task DeleteDirectoriesUnix(IRenderer renderer, IEnumerable<AbsolutePath> appDirectories)
    {
        foreach (var directory in appDirectories)
        {
            if (!directory.DirectoryExists())
                continue;

            try
            {
                directory.DeleteDirectoryNoFollow();
                await renderer.Text("Deleted directory: {0}", directory);
            }
            catch (Exception e)
            {
                await renderer.Error(e, "Failed to delete directory: {0}", directory); 
            }
        }
    }

    private static async Task DeleteFilesUnix(IRenderer renderer, AbsolutePath[] appFiles)
    {
        foreach (var appFile in appFiles)
        {
            if (!appFile.FileExists)
                continue;

            try
            {
                appFile.Delete();
                await renderer.Text("Deleted file: {0}", appFile);
            }
            catch (Exception e)
            {
                await renderer.Error(e, "Failed to delete file: {0}", appFile); 
            }
        }
    }
}
