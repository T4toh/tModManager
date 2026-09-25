using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// The synchronizer must never read, write or delete outside the game folder: not through a symlinked folder,
/// not through a <c>..</c> in a mod path, and never delete the game when the vanilla file list is unknown.
/// </summary>
public class DiskSafetyTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<DiskSafetyTests>(helper)
{
    private async Task<(AbsolutePath Outside, AbsolutePath Canary)> LinkedFolderInsideGame(string relative)
    {
        var outside = TemporaryFileManager.CreateFolder().Path;
        var canary = outside.Combine("sub/precious.archive");
        canary.Parent.CreateDirectory();
        await canary.WriteAllTextAsync("user data");

        var link = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, relative));
        link.Parent.CreateDirectory();
        File.CreateSymbolicLink(link.ToString(), outside.ToString());
        return (outside, canary);
    }

    private async Task<Loadout.ReadOnly> ManagedLoadout()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.ReindexState(GameInstallation);
        return await Synchronizer.Synchronize(await CreateLoadout());
    }

    [Fact]
    public async Task SymlinkedFolderInsideGame_IsNeverIndexedOrDeleted()
    {
        var loadout = await ManagedLoadout();
        var (_, canary) = await LinkedFolderInsideGame("bin/linked");

        loadout = await Synchronizer.Synchronize(loadout);

        DiskStateEntry.FindByGame(loadout.Installation.Db, loadout.Installation)
            .Should().NotContain(e => e.Path.Item3.ToString().StartsWith("bin/linked"));

        // Unmanaging resets the game folder to vanilla, deleting everything it indexed that isn't vanilla
        await LoadoutManager.UnManage(GameInstallation);
        canary.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ModFileUnderASymlinkedFolder_FailsTheSyncWithoutWritingOutside()
    {
        var loadout = await ManagedLoadout();
        var (outside, _) = await LinkedFolderInsideGame("bin/linked");
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"bin/linked/mod.txt"], loadout, "LinkedMod");
            await tx.Commit();
        }
        Refresh(ref loadout);

        var act = () => Synchronizer.Synchronize(loadout);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*symlink*");
        outside.Combine("mod.txt").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task ModFileWithParentSegments_FailsTheSyncWithoutWritingOutside()
    {
        // FOMOD destinations and collection.json paths can carry `..`
        var gameRoot = GameInstallation.Locations[LocationId.Game].Path;
        var escaped = gameRoot.Parent.Combine($"escaped-{Guid.NewGuid():N}.txt");
        var loadout = await ManagedLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)$"../{escaped.FileName}"], loadout, "EscapingMod");
            await tx.Commit();
        }
        Refresh(ref loadout);

        var act = () => Synchronizer.Synchronize(loadout);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fuera de la carpeta del juego*");
        escaped.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task ResetWithUnknownLocatorIds_RefusesAndDeletesNothing()
    {
        // A Steam patch the hash database doesn't know, or a manually added game: no vanilla list at all
        var loadout = await ManagedLoadout();
        var original = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "bin/x64/original.exe"));
        original.Parent.CreateDirectory();
        await original.WriteAllTextAsync("vanilla");
        await Synchronizer.Synchronize(loadout);

        var act = () => Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("999999999999")]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lista de archivos originales*");
        original.FileExists.Should().BeTrue();
    }
}
