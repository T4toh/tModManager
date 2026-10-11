using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

/// <summary>
/// The REDmod load order is a file the loadout owns: written on apply, pending when the order changes,
/// restored when edited by hand, never written through a symlink, backed up when it was there first.
/// </summary>
public class SortOrderFileTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<SortOrderFileTests>(helper)
{
    protected override bool WithWinePrefix => true;

    private AbsolutePath Prefix => GameInstallation.Locations[LocationId.WinePrefix].Path;
    private AbsolutePath ModList => Prefix.Combine("drive_c/modlist.txt");

    private async Task<Loadout.ReadOnly> ManagedLoadout()
    {
        _ = InitAndGetSortOrderManager();
        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.ReindexState(GameInstallation);
        return await Synchronizer.Synchronize(await CreateLoadout());
    }

    private async Task<Loadout.ReadOnly> AddRedMods(Loadout.ReadOnly loadout)
    {
        await using var tempDir = TemporaryFileManager.CreateFolder();
        foreach (var file in new[] { "one_mod.7z", "several_red_mods.7z" })
        {
            var sourcePath = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("LibraryArchiveInstallerTests/Resources/" + file);
            var copyPath = tempDir.Path.Combine(file);
            File.Copy(sourcePath.ToString(), copyPath.ToString(), overwrite: true);
            var archive = await RegisterLocalArchive(copyPath);
            await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout);
        }
        // The SortOrderManager reacts to loadout changes asynchronously
        await Task.Delay(TimeSpan.FromSeconds(2));
        return loadout.Rebase();
    }

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout)
    {
        Refresh(ref loadout);
        return await Synchronizer.Synchronize(loadout);
    }

    private string Rendered(Loadout.ReadOnly loadout)
    {
        loadout = loadout.Rebase();
        return ServiceProvider.GetRequiredService<RedModSortOrderVariety>().Render(loadout, loadout.Db);
    }

    // Same shape as IntrinsicSettingsFileTests.ShouldSync
    private bool NeedsSync(Loadout.ReadOnly loadout)
    {
        Refresh(ref loadout);
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        var sync = (ALoadoutSynchronizer)GameInstallation.GetGame().Synchronizer;
        var disk = sync.GetDiskStateForGame(metadata);
        var previous = ((ILoadoutSynchronizer)sync).GetPreviouslyAppliedDiskState(metadata);
        return sync.ShouldSynchronize(loadout, previous, disk);
    }

    private int ExternalChangesCount(Loadout.ReadOnly loadout)
    {
        loadout = loadout.Rebase();
        return LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children)
            .Count();
    }

    [Fact]
    public async Task Apply_WritesTheEnabledRedModsInOrder_WithCrlf()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);

        loadout = await Apply(loadout);

        ModList.FileExists.Should().BeTrue();
        var text = await ModList.ReadAllTextAsync();
        text.Should().Be(Rendered(loadout));
        text.Should().Contain("\r\n").And.NotContain("\n\n");
        text.Split("\r\n").Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task NoRedMods_NoFileIsCreated()
    {
        var loadout = await ManagedLoadout();

        loadout = await Apply(loadout);

        ModList.FileExists.Should().BeFalse("an empty order is nothing to write");
        NeedsSync(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task DisablingARedMod_RemovesItFromTheFile()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        var before = await ModList.ReadAllTextAsync();
        before.Should().Contain("Driver_Shotguns");

        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();
        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId).Value;
        var group = variety.GetSortOrderItems(sortOrderId, Connection.Db).Single(i => i.DisplayName == "Driver_Shotguns").ModGroupId!.Value;
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Task.Delay(TimeSpan.FromSeconds(2));

        NeedsSync(loadout).Should().BeTrue("the order changed, the file on disk is stale");
        loadout = await Apply(loadout);

        (await ModList.ReadAllTextAsync()).Should().NotContain("Driver_Shotguns");
        NeedsSync(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task ReorderingInTheLoadout_IsPending_AndApplyRewrites()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        var before = await ModList.ReadAllTextAsync();
        NeedsSync(loadout).Should().BeFalse();

        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();
        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId).Value;
        var first = variety.GetSortOrderItems(sortOrderId, Connection.Db).First();
        await variety.MoveItemDelta(sortOrderId, first.Key, 3);

        NeedsSync(loadout).Should().BeTrue();
        loadout = await Apply(loadout);

        var after = await ModList.ReadAllTextAsync();
        after.Should().NotBe(before);
        after.Should().Be(Rendered(loadout));
        NeedsSync(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task HandEdit_IsOverwrittenWithoutExternalChange()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        var wanted = await ModList.ReadAllTextAsync();

        // Same mods, LF line endings and reversed: REDmod would choke on the LF
        await ModList.WriteAllTextAsync(string.Join("\n", wanted.Split("\r\n").Reverse()));
        NeedsSync(loadout).Should().BeTrue();

        loadout = await Apply(loadout);

        (await ModList.ReadAllTextAsync()).Should().Be(wanted);
        ExternalChangesCount(loadout).Should().Be(0, "the game never writes this file, the loadout simply wins");
    }

    [Fact]
    public async Task PrefixMissing_NoFileNoErrorNoDeploy()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        Prefix.DeleteDirectoryNoFollow();

        var act = () => Apply(loadout);

        await act.Should().NotThrowAsync();
        Prefix.DirectoryExists().Should().BeFalse("the app never recreates the prefix");
    }

    [Fact]
    public async Task PrefixDeletedAfterApply_SyncDoesNotThrow()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        ModList.FileExists.Should().BeTrue();

        // Storage Manager "Borrar prefix de Proton"
        Prefix.DeleteDirectoryNoFollow();
        var act = () => Apply(loadout);

        await act.Should().NotThrowAsync();
        Prefix.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task SymlinkedModlist_IsNeverWrittenThrough()
    {
        var outside = TemporaryFileManager.CreateFolder().Path.Combine("modlist.txt");
        await outside.WriteAllTextAsync("precious");
        ModList.Parent.CreateDirectory();
        File.CreateSymbolicLink(ModList.ToString(), outside.ToString());
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);

        var act = () => Apply(loadout);

        await act.Should().NotThrowAsync("the intrinsic is simply not declared");
        (await outside.ReadAllTextAsync()).Should().Be("precious");
        new FileInfo(ModList.ToString()).LinkTarget.Should().NotBeNull("the link itself is left alone");
    }

    [Fact]
    public async Task PreexistingModlist_PrefixDeletedAfterApply_IsNotRecreated()
    {
        // An adopted original (layer 0) must not be extracted back into a prefix that is gone
        ModList.Parent.CreateDirectory();
        await ModList.WriteAllTextAsync("vortex_mod\r\n");
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);

        Prefix.DeleteDirectoryNoFollow();
        loadout = await Apply(loadout);

        Prefix.DirectoryExists().Should().BeFalse("the app never recreates the prefix, not even for an original");
        NeedsSync(loadout).Should().BeFalse("nothing is pending inside a prefix that is not there");
    }

    [Fact]
    public async Task PreexistingModlist_ReplacedBySymlinkAfterApply_ApplyContinues()
    {
        ModList.Parent.CreateDirectory();
        await ModList.WriteAllTextAsync("vortex_mod\r\n");
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);

        var outside = TemporaryFileManager.CreateFolder().Path.Combine("modlist.txt");
        await outside.WriteAllTextAsync("precious");
        ModList.Delete();
        File.CreateSymbolicLink(ModList.ToString(), outside.ToString());

        var act = () => Apply(loadout);

        await act.Should().NotThrowAsync("a linked whitelisted file is simply not ours any more");
        (await outside.ReadAllTextAsync()).Should().Be("precious");
        new FileInfo(ModList.ToString()).LinkTarget.Should().NotBeNull();
    }

    [Fact]
    public async Task PreexistingModlist_IsBackedUpAndRestoredOnUnManage()
    {
        // Written by another manager before tModManager took over
        ModList.Parent.CreateDirectory();
        await ModList.WriteAllTextAsync("vortex_mod\r\n");
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        (await ModList.ReadAllTextAsync()).Should().NotBe("vortex_mod\r\n");

        await LoadoutManager.UnManage(GameInstallation);

        (await ModList.ReadAllTextAsync()).Should().Be("vortex_mod\r\n");
    }
}
