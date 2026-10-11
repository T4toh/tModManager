using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// The Wine prefix is a location with a whitelist: only the listed files are indexed, written or deleted, never
/// through a symlink, and nothing else inside the prefix is ever touched.
/// </summary>
public class WinePrefixLocationTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<WinePrefixLocationTests>(helper)
{
    protected override bool WithWinePrefix => true;

    private const string SettingsFolder = "drive_c/users/steamuser/AppData/Local/CD Projekt Red/Cyberpunk 2077";
    private const string Settings = SettingsFolder + "/UserSettings.json";
    private static readonly GamePath SettingsPath = new(LocationId.WinePrefix, Settings);

    private AbsolutePath Prefix => GameInstallation.Locations[LocationId.WinePrefix].Path;
    private AbsolutePath PrefixFile(string relative) => Prefix.Combine(relative);

    private async Task WritePrefixFile(string relative, string content)
    {
        var file = PrefixFile(relative);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(content);
    }

    private async Task<Loadout.ReadOnly> ManagedLoadout()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.ReindexState(GameInstallation);
        return await Synchronizer.Synchronize(await CreateLoadout());
    }

    private GamePath[] IndexedPrefixPaths()
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        return DiskStateEntry.FindByGame(metadata.Db, metadata)
            .Select(e => (GamePath)e.Path)
            .Where(p => p.LocationId == LocationId.WinePrefix)
            .ToArray();
    }

    [Fact]
    public void ThePrefixIsALocationWithAWhitelist()
    {
        GameInstallation.Locations[LocationId.WinePrefix].Path.ToString().Should().EndWith("/pfx");
        GameInstallation.Locations[LocationId.WinePrefix].ManagedFiles.Should().BeEquivalentTo([(RelativePath)Settings]);
        GameInstallation.Locations[LocationId.Game].ManagedFiles.Should().BeNull();
    }

    [Fact]
    public async Task IndexesOnlyTheWhitelist()
    {
        await WritePrefixFile(Settings, "{}");
        await WritePrefixFile(SettingsFolder + "/CrashInfo.json", "crash");
        await WritePrefixFile(SettingsFolder + "/cache/x", "cache");
        await WritePrefixFile("drive_c/windows/system32/a.dll", "dll");

        await ManagedLoadout();

        IndexedPrefixPaths().Should().Equal(SettingsPath);
    }

    [Fact]
    public async Task PrefixDeletedAfterManaging_SyncDoesNotThrow()
    {
        await WritePrefixFile(Settings, "{}");
        var loadout = await ManagedLoadout();
        IndexedPrefixPaths().Should().Equal(SettingsPath);

        // Storage Manager "Borrar prefix de Proton", or Steam recreating it
        Prefix.DeleteDirectoryNoFollow();

        loadout = await Synchronizer.Synchronize(loadout.Rebase());

        IndexedPrefixPaths().Should().BeEmpty();
        loadout.IsValid().Should().BeTrue();
    }

    private const string OriginalJson = "{\"version\":140,\"data\":[{\"group_name\":\"/g\",\"options\":[{\"name\":\"k\",\"value\":1}]}]}";

    /// <summary>A mod that owns one key of the (intrinsic) settings file.</summary>
    private async Task<Loadout.ReadOnly> WithSettingsEntry(Loadout.ReadOnly loadout, string key, string value)
    {
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "SettingsMod");
            _ = new IntrinsicFileEntry.New(tx, out var id)
            {
                File = SettingsPath, Key = key, Value = value,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = key, LoadoutId = loadout.LoadoutId, ParentId = group },
            };
            await tx.Commit();
        }
        Refresh(ref loadout);
        return loadout;
    }

    private async Task<Loadout.ReadOnly> WithSettingsMod(Loadout.ReadOnly loadout)
    {
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [SettingsPath], loadout, "SettingsMod");
            await tx.Commit();
        }
        Refresh(ref loadout);
        return loadout;
    }

    /// <summary>Canary folder outside the prefix with the settings chain inside it, and a symlink at <paramref name="linkRelative"/> pointing to it.</summary>
    private async Task<(AbsolutePath Outside, AbsolutePath Settings, AbsolutePath Canary)> LinkedOutside(string linkRelative, string insideSettings)
    {
        var outside = TemporaryFileManager.CreateFolder().Path;
        var settings = outside.Combine(insideSettings);
        settings.Parent.CreateDirectory();
        await settings.WriteAllTextAsync("user data");
        var canary = outside.Combine("precious.txt");
        await canary.WriteAllTextAsync("precious");

        var link = PrefixFile(linkRelative);
        link.Parent.CreateDirectory();
        File.CreateSymbolicLink(link.ToString(), outside.ToString());
        return (outside, settings, canary);
    }

    [Fact]
    public async Task ResetRestoresTheSettingsAndLeavesTheRest()
    {
        // UserSettings.json is an intrinsic settings file: a mod owns a key, never the whole file
        await WritePrefixFile(Settings, OriginalJson);
        await WritePrefixFile(SettingsFolder + "/CrashInfo.json", "crash");
        var loadout = await ManagedLoadout();
        loadout = await WithSettingsEntry(loadout, "/g/k", "2");

        loadout = await Synchronizer.Synchronize(loadout);
        (await PrefixFile(Settings).ReadAllTextAsync()).Should().Contain("\"value\": 2");

        // The game edits its settings between syncs
        await PrefixFile(Settings).WriteAllTextAsync(OriginalJson.Replace("\"value\":1", "\"value\":3", StringComparison.Ordinal));

        await LoadoutManager.UnManage(GameInstallation);

        (await PrefixFile(Settings).ReadAllTextAsync()).Should().Be(OriginalJson);
        (await PrefixFile(SettingsFolder + "/CrashInfo.json").ReadAllTextAsync()).Should().Be("crash");
        PrefixFile(SettingsFolder).DirectoryExists().Should().BeTrue("the app never removes folders inside the prefix");
    }

    [Fact]
    public async Task SettingsFileCreatedByTheLoadout_UnManageRemovesTheFile_NeverItsFolders()
    {
        // Wine created the user folders, the game never wrote its settings yet: the only file in the chain is ours
        PrefixFile(SettingsFolder).CreateDirectory();
        var loadout = await ManagedLoadout();
        loadout = await WithSettingsEntry(loadout, "/g/k", "2");
        await Synchronizer.Synchronize(loadout);
        PrefixFile(Settings).FileExists.Should().BeTrue();

        await LoadoutManager.UnManage(GameInstallation);

        PrefixFile(Settings).FileExists.Should().BeFalse("nothing but the loadout ever put it there");
        PrefixFile(SettingsFolder).DirectoryExists().Should().BeTrue("folders inside the prefix are Wine's, never cleaned as empty");
        PrefixFile("drive_c/users/steamuser").DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task ModFileOutsideTheWhitelist_FailsTheSyncWithoutWriting()
    {
        var loadout = await ManagedLoadout();
        var outsideWhitelist = new GamePath(LocationId.WinePrefix, "drive_c/users/steamuser/Desktop/x.txt");
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [outsideWhitelist], loadout, "DesktopMod");
            await tx.Commit();
        }
        Refresh(ref loadout);

        var act = () => Synchronizer.Synchronize(loadout);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no está entre los archivos*");
        PrefixFile("drive_c/users/steamuser/Desktop/x.txt").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task SymlinkedSettingsFolder_IsNeverWrittenThroughNorIndexed()
    {
        var (_, outsideSettings, canary) = await LinkedOutside(SettingsFolder, "UserSettings.json");
        var loadout = await ManagedLoadout();
        IndexedPrefixPaths().Should().BeEmpty();

        loadout = await WithSettingsMod(loadout);
        var act = () => Synchronizer.Synchronize(loadout);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*symlink*");
        (await outsideSettings.ReadAllTextAsync()).Should().Be("user data");
        canary.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task SymlinkedUserFolder_IsNeverIndexedNorDeleted()
    {
        // A Wine prefix can link the whole user folder, or Documents/Desktop, to the real $HOME
        var (_, outsideSettings, canary) = await LinkedOutside("drive_c/users/steamuser", "AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json");

        await ManagedLoadout();
        IndexedPrefixPaths().Should().BeEmpty();

        await LoadoutManager.UnManage(GameInstallation);
        (await outsideSettings.ReadAllTextAsync()).Should().Be("user data");
        canary.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task WhitelistedFileThatIsASymlink_IsNeverIndexedNorWritten()
    {
        var outside = TemporaryFileManager.CreateFolder().Path.Combine("UserSettings.json");
        await outside.WriteAllTextAsync("user data");
        PrefixFile(Settings).Parent.CreateDirectory();
        File.CreateSymbolicLink(PrefixFile(Settings).ToString(), outside.ToString());

        var loadout = await ManagedLoadout();
        IndexedPrefixPaths().Should().BeEmpty();

        loadout = await WithSettingsMod(loadout);
        var act = () => Synchronizer.Synchronize(loadout);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*symlink*");
        (await outside.ReadAllTextAsync()).Should().Be("user data");
    }

    private GamePath[] BaselinePrefixPaths()
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        return GameBaselineFile.FindByGame(metadata.Db, metadata)
            .Select(e => (GamePath)e.Path)
            .Where(p => p.LocationId == LocationId.WinePrefix)
            .ToArray();
    }

    private int ExternalChangesCount(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Count(i => ((GamePath)i.TargetPath).LocationId == LocationId.WinePrefix);

    [Fact]
    public async Task FileTheGameCreatesAfterManaging_IsAnOriginal_AndSurvivesUnManage()
    {
        // Same shape as an install managed before the prefix location existed: the file is already there, the
        // list is already built, and the app sees the file for the first time
        var loadout = await ManagedLoadout();
        await WritePrefixFile(Settings, "created by the game");

        loadout = await Synchronizer.Synchronize(loadout.Rebase());

        BaselinePrefixPaths().Should().Equal(SettingsPath);
        ExternalChangesCount(loadout).Should().Be(0, "a file nobody but the game writes is an original, not an External Change");
        await LoadoutManager.UnManage(GameInstallation);
        (await PrefixFile(Settings).ReadAllTextAsync()).Should().Be("created by the game");
    }

    [Fact]
    public async Task GameEditWithoutAMod_ResetRestoresTheOriginal()
    {
        await WritePrefixFile(Settings, OriginalJson);
        var loadout = await ManagedLoadout();

        await PrefixFile(Settings).WriteAllTextAsync(OriginalJson.Replace("\"value\":1", "\"value\":3", StringComparison.Ordinal));
        loadout = await Synchronizer.Synchronize(loadout.Rebase());
        ExternalChangesCount(loadout).Should().Be(0, "a settings file is intrinsic: the game's edits become its base, not a file-level External Change");

        await LoadoutManager.UnManage(GameInstallation);

        (await PrefixFile(Settings).ReadAllTextAsync()).Should().Be(OriginalJson, "originals in the prefix are backed up when first seen");
    }
}
