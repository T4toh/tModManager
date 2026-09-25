using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using Xunit;

namespace NexusMods.Library.Tests;

public class DownloadsFolderTests : IDisposable
{
    private readonly AbsolutePath _root = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"dl-{Guid.NewGuid():N}");
    public void Dispose() { if (_root.DirectoryExists()) _root.DeleteDirectory(true); }

    private async Task<AbsolutePath> Temp(string content)
    {
        var p = _root.Combine("tmp").Combine(Guid.NewGuid().ToString("N"));
        p.Parent.CreateDirectory();
        await File.WriteAllTextAsync(p.ToString(), content);
        return p;
    }

    [Fact]
    public async Task Preserve_NewName_CopiesWithNexusName()
    {
        var dest = await DownloadsFolder.PlaceAsync(await Temp("a"), _root.Combine("Downloads"), "Mod-123-1-0.zip", default);
        dest.FileName.ToString().Should().Be("Mod-123-1-0.zip");
        (await File.ReadAllTextAsync(dest.ToString())).Should().Be("a");
    }

    [Fact]
    public async Task Preserve_SameNameSameContent_ReusesExisting()
    {
        var folder = _root.Combine("Downloads");
        var first = await DownloadsFolder.PlaceAsync(await Temp("a"), folder, "Mod.zip", default);
        var second = await DownloadsFolder.PlaceAsync(await Temp("a"), folder, "Mod.zip", default);
        second.Should().Be(first);
        folder.EnumerateFiles("*", recursive: false).Should().ContainSingle();
    }

    [Fact]
    public async Task Preserve_SameNameDifferentContent_GetsSuffix()
    {
        var folder = _root.Combine("Downloads");
        await DownloadsFolder.PlaceAsync(await Temp("v1"), folder, "Mod.zip", default);
        var second = await DownloadsFolder.PlaceAsync(await Temp("v2"), folder, "Mod.zip", default);
        second.FileName.ToString().Should().Be("Mod_1.zip");
        (await File.ReadAllTextAsync(folder.Combine("Mod.zip").ToString())).Should().Be("v1");
    }

    [Fact]
    public async Task TryClaim_CandidateAppearedMeanwhile_ReturnsFalseAndLeavesItUntouched()
    {
        // Simulates the race PlaceAsync's loop handles: another writer's File.Move wins between
        // our existence check and ours. No timing needed: pre-create the candidate and call the
        // move-or-advance helper directly, exactly like a losing PlaceAsync call would hit it.
        var folder = _root.Combine("Downloads");
        folder.CreateDirectory();
        var candidate = folder.Combine("Mod.zip");
        await File.WriteAllTextAsync(candidate.ToString(), "existing");

        var claimed = await DownloadsFolder.TryClaimAsync(await Temp("new"), candidate, default);

        claimed.Should().BeFalse();
        (await File.ReadAllTextAsync(candidate.ToString())).Should().Be("existing");
        folder.EnumerateFiles("*", recursive: false).Should().ContainSingle();
    }

    [Fact]
    public async Task TryClaim_MoveFailsForNonCollisionReason_Throws()
    {
        // Not every IOException from File.Move means "someone else claimed the name": occupy the
        // candidate with a directory instead of a file, so the move fails but `candidate.FileExists`
        // (a plain-file check) stays false, exactly like disk-full or a read-only filesystem would.
        // This must propagate instead of being swallowed as a race and retried forever.
        var folder = _root.Combine("Downloads");
        folder.CreateDirectory();
        var candidate = folder.Combine("Mod.zip");
        candidate.CreateDirectory();

        var act = async () => await DownloadsFolder.TryClaimAsync(await Temp("new"), candidate, default);

        await act.Should().ThrowAsync<IOException>();
        candidate.FileExists.Should().BeFalse();
    }

    [Theory]
    [InlineData("../evil.zip", "evil.zip")]
    [InlineData("sub/dir/mod.zip", "mod.zip")]
    public async Task Preserve_UntrustedName_SanitizedToLastSegmentInsideFolder(string untrustedName, string expectedName)
    {
        var folder = _root.Combine("Downloads");
        var dest = await DownloadsFolder.PlaceAsync(await Temp("a"), folder, untrustedName, default);

        dest.Parent.Should().Be(folder);
        dest.FileName.ToString().Should().Be(expectedName);
    }

    [Theory]
    [InlineData("..\\..\\evil.zip")]
    [InlineData("../../evil.zip")]
    [InlineData("sub\\evil.zip")]
    public async Task Place_NameWithSeparators_StaysInsideTheFolder(string fileName)
    {
        var folder = _root.Combine("a/b/Downloads");

        var dest = await DownloadsFolder.PlaceAsync(await Temp("a"), folder, fileName, default);

        dest.Parent.Should().Be(folder);
        dest.FileName.ToString().Should().Be("evil.zip");
    }
}
