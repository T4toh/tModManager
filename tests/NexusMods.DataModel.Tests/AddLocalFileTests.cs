using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class AddLocalFileTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<AddLocalFileTests>(helper)
{
    private AbsolutePath Downloads => ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);

    private AbsolutePath Outside(string name, string content = "mod bytes")
    {
        var folder = TemporaryFileManager.CreateFolder().Path;
        var file = folder.Combine(name);
        File.WriteAllText(file.ToString(), content);
        return file;
    }

    private static string[] FilesIn(AbsolutePath folder) =>
        folder.DirectoryExists() ? folder.EnumerateFiles("*", recursive: false).Select(f => f.FileName.ToString()).Order(StringComparer.Ordinal).ToArray() : [];

    [Fact]
    public async Task AddLocalFile_OutsideDownloads_IsCopiedAndRegistered()
    {
        var src = Outside("Mod.zip");

        var local = await LibraryService.AddLocalFile(src);

        src.FileExists.Should().BeTrue("the user's file is never moved");
        FilesIn(Downloads).Should().Equal("Mod.zip");
        LibraryFile.DownloadPath.Get(local.AsLibraryFile()).ToString().Should().Be("Mod.zip");
        local.OriginalPath.Should().Be(src.ToString());
        (await FileStore.HaveFile(local.AsLibraryFile().Hash)).Should().BeTrue();
    }

    [Fact]
    public async Task AddLocalFile_InsideDownloads_IsNotCopied()
    {
        Downloads.CreateDirectory();
        var inside = Downloads.Combine("Inside.zip");
        File.WriteAllText(inside.ToString(), "x");

        var local = await LibraryService.AddLocalFile(inside);

        FilesIn(Downloads).Should().Equal("Inside.zip");
        LibraryFile.DownloadPath.Get(local.AsLibraryFile()).ToString().Should().Be("Inside.zip");
    }

    [Fact]
    public async Task AddLocalFile_DownloadsFolderMissing_CreatesIt()
    {
        Downloads.DirectoryExists().Should().BeFalse();
        await LibraryService.AddLocalFile(Outside("Mod.zip"));
        Downloads.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task AddLocalFile_SameNameDifferentContent_GetsSuffix()
    {
        var first = await LibraryService.AddLocalFile(Outside("Mod.zip", "v1"));
        var second = await LibraryService.AddLocalFile(Outside("Mod.zip", "v2"));

        FilesIn(Downloads).Should().Equal("Mod.zip", "Mod_1.zip");
        LibraryFile.DownloadPath.Get(second.AsLibraryFile()).ToString().Should().Be("Mod_1.zip");
        second.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task AddLocalFile_SameHashTwice_ReturnsExistingAndLeavesNoCopy()
    {
        var first = await LibraryService.AddLocalFile(Outside("A.zip", "same"));
        var second = await LibraryService.AddLocalFile(Outside("B.zip", "same"));

        second.Id.Should().Be(first.Id);
        FilesIn(Downloads).Should().Equal("A.zip");
        LocalFile.All(Connection.Db).Should().ContainSingle();
    }

    [Fact]
    public async Task AddLocalFile_SameFileTwice_ReturnsExistingAndLeavesOneCopy()
    {
        var src = Outside("Mod.zip");
        var first = await LibraryService.AddLocalFile(src);
        var second = await LibraryService.AddLocalFile(src);

        second.Id.Should().Be(first.Id);
        FilesIn(Downloads).Should().Equal("Mod.zip");
    }

    [Fact]
    public async Task AddLocalFile_SameHashAsOldLocalWithoutDownloadPath_RepairsIt()
    {
        // A LocalFile from before this change: registered from outside Downloads, no DownloadPath.
        var old = Outside("Old.zip", "old bytes");
        using (var tx = Connection.BeginTransaction())
        {
            var lib = new LibraryFile.New(tx, out var id)
            {
                FileName = old.FileName,
                Hash = await HashOf(old),
                Size = old.FileInfo.Size,
                LibraryItem = new LibraryItem.New(tx, id) { Name = old.FileName },
            };
            _ = new LocalFile.New(tx, id) { LibraryFile = lib, OriginalPath = old.ToString() };
            await tx.Commit();
        }

        var local = await LibraryService.AddLocalFile(Outside("Again.zip", "old bytes"));

        LocalFile.All(Connection.Db).Should().ContainSingle();
        LibraryFile.DownloadPath.Get(local.AsLibraryFile()).ToString().Should().Be("Again.zip");
        FilesIn(Downloads).Should().Equal("Again.zip");
    }

    [Fact]
    public async Task AddLocalFile_SymlinkToFile_CopiesARegularFile()
    {
        var target = Outside("real.zip", "linked bytes");
        var link = TemporaryFileManager.CreateFolder().Path.Combine("Link.zip");
        File.CreateSymbolicLink(link.ToString(), target.ToString());

        await LibraryService.AddLocalFile(link);

        var copy = Downloads.Combine("Link.zip");
        copy.FileExists.Should().BeTrue();
        new FileInfo(copy.ToString()).LinkTarget.Should().BeNull("Downloads must hold a regular file, never a link");
        File.ReadAllText(copy.ToString()).Should().Be("linked bytes");
        File.ReadAllText(target.ToString()).Should().Be("linked bytes");
    }

    [Fact]
    public async Task AddLocalFile_SymlinkToDirectory_IsRejected()
    {
        var dir = TemporaryFileManager.CreateFolder().Path;
        File.WriteAllText(dir.Combine("canary.txt").ToString(), "canary");
        var link = TemporaryFileManager.CreateFolder().Path.Combine("Mod.zip");
        Directory.CreateSymbolicLink(link.ToString(), dir.ToString());

        var act = async () => await LibraryService.AddLocalFile(link);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{link}*");
        FilesIn(Downloads).Should().BeEmpty();
        LocalFile.All(Connection.Db).Should().BeEmpty();
        File.ReadAllText(dir.Combine("canary.txt").ToString()).Should().Be("canary");
    }

    [Fact]
    public async Task AddLocalFile_WithMetadata_StoresEverything()
    {
        var meta = new LocalFileMetadata(Name: "Mi mod", Version: "1.2.3", Source: "GitHub", PageUri: new Uri("https://github.com/x/y"));

        var local = await LibraryService.AddLocalFile(Outside("Mod.zip"), meta);

        local.AsLibraryFile().AsLibraryItem().Name.Should().Be("Mi mod");
        LocalFile.Version.Get(local).Should().Be("1.2.3");
        LocalFile.Source.Get(local).Should().Be("GitHub");
        LocalFile.PageUri.Get(local).Should().Be(new Uri("https://github.com/x/y"));
    }

    [Fact]
    public async Task AddLocalFile_EmptyMetadata_StoresNothing()
    {
        var local = await LibraryService.AddLocalFile(Outside("Mod.zip"));

        local.AsLibraryFile().AsLibraryItem().Name.Should().Be("Mod.zip");
        LocalFile.Version.TryGetValue(local, out _).Should().BeFalse();
        LocalFile.Source.TryGetValue(local, out _).Should().BeFalse();
        LocalFile.PageUri.TryGetValue(local, out _).Should().BeFalse();
    }

    [Fact]
    public async Task AddLocalFile_WhitespaceMetadata_StoresNothing()
    {
        var local = await LibraryService.AddLocalFile(Outside("Mod.zip"), new LocalFileMetadata(Name: "  ", Version: "\t", Source: ""));

        local.AsLibraryFile().AsLibraryItem().Name.Should().Be("Mod.zip");
        LocalFile.Version.TryGetValue(local, out _).Should().BeFalse();
        LocalFile.Source.TryGetValue(local, out _).Should().BeFalse();
    }

    private static async Task<Hash> HashOf(AbsolutePath path)
    {
        await using var stream = path.Read();
        return await stream.xxHash3Async();
    }
}
