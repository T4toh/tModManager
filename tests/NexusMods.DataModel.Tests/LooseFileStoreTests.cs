using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.IO;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class LooseFileStoreTests : IDisposable
{
    private readonly AbsolutePath _root = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory)
        .Combine($"LooseFileStoreTests-{Guid.NewGuid():N}");
    private readonly AbsolutePath _out;
    private readonly LooseFileStore _store;

    public LooseFileStoreTests()
    {
        _out = _root.Combine("out");
        _store = new LooseFileStore(NullLogger<LooseFileStore>.Instance, _root.Combine("store"));
    }

    public void Dispose() { if (_root.DirectoryExists()) _root.DeleteDirectory(true); }

    private AbsolutePath StoreRoot => _root.Combine("store");

    private static ArchivedFileEntry Entry(string text, Hash? hashOverride = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new ArchivedFileEntry(new MemoryStreamFactory("x", new MemoryStream(bytes)), hashOverride ?? bytes.xxHash3(), Size.FromLong(bytes.Length));
    }

    [Fact]
    public async Task BackupFiles_ThenHaveFileAndLoad()
    {
        var e = Entry("hola");
        (await _store.HaveFile(e.Hash)).Should().BeFalse();

        await _store.BackupFiles([e]);

        (await _store.HaveFile(e.Hash)).Should().BeTrue();
        (await _store.Load(e.Hash)).Should().Equal(Encoding.UTF8.GetBytes("hola"));
        _store.PathFor(e.Hash).Parent.FileName.ToString().Should().Be(e.Hash.ToHex()[..2]);
    }

    [Fact]
    public async Task BackupFiles_SameHashTwice_KeepsOneFile()
    {
        var e = Entry("dup");
        await _store.BackupFiles([e, e]);
        await _store.BackupFiles([e]);
        _store.PathFor(e.Hash).Parent.EnumerateFiles("*", recursive: false).Should().ContainSingle();
    }

    [Fact]
    public async Task BackupFiles_HashMismatch_LeavesNoFile()
    {
        var bad = Entry("contenido", hashOverride: Hash.From(42));
        var act = async () => await _store.BackupFiles([bad]);
        await act.Should().ThrowAsync<InvalidDataException>();
        (await _store.HaveFile(Hash.From(42))).Should().BeFalse();
        _store.PathFor(Hash.From(42)).Parent.EnumerateFiles("*", recursive: false).Should().BeEmpty("no .tmp leftovers");
    }

    [Fact]
    public async Task BackupFiles_ConcurrentSameHash_BothSucceed()
    {
        var a = Entry("carrera");
        var b = Entry("carrera");
        await Task.WhenAll(_store.BackupFiles([a]), _store.BackupFiles([b]));
        (await _store.Load(a.Hash)).Should().Equal(Encoding.UTF8.GetBytes("carrera"));
    }

    [Fact]
    public async Task ExtractFiles_CopiesAndCreatesEmptyFiles()
    {
        var e = Entry("mod");
        await _store.BackupFiles([e]);
        var empty = Array.Empty<byte>().xxHash3();
        var dest1 = _out.Combine("a/b/file.txt");
        var dest2 = _out.Combine("c/empty.txt");

        await _store.ExtractFiles([(e.Hash, dest1), (empty, dest2)]);

        (await dest1.ReadAllTextAsync()).Should().Be("mod");
        dest2.FileExists.Should().BeTrue();
        dest2.FileInfo.Size.Should().Be(Size.Zero);
    }

    [Fact]
    public async Task ExtractFiles_MissingHash_ThrowsBeforeWritingAnything()
    {
        var present = Entry("ok");
        await _store.BackupFiles([present]);
        var destOk = _out.Combine("ok.txt");

        var act = async () => await _store.ExtractFiles([(present.Hash, destOk), (Hash.From(7), _out.Combine("missing.txt"))]);

        (await act.Should().ThrowAsync<MissingArchiveException>()).WithMessage("*1*");
        destOk.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task GetFileStream_IsSeekable()
    {
        var e = Entry("stream");
        await _store.BackupFiles([e]);
        await using var s = await _store.GetFileStream(e.Hash);
        s.CanSeek.Should().BeTrue();
        s.Length.Should().Be(6);
    }

    [Fact]
    public async Task DeleteAllExcept_KeepsLiveDeletesRest()
    {
        var keep = Entry("vivo");
        var drop = Entry("muerto");
        await _store.BackupFiles([keep, drop]);
        _store.GracePeriod = TimeSpan.Zero;

        var deleted = _store.DeleteAllExcept(new HashSet<Hash> { keep.Hash });

        deleted.Should().Be(1);
        (await _store.HaveFile(keep.Hash)).Should().BeTrue();
        (await _store.HaveFile(drop.Hash)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAllExcept_KeepsYoungUnreferencedFiles()
    {
        // A file with no DB reference yet may belong to a backup whose commit just hasn't landed.
        var drop = Entry("recien_escrito");
        await _store.BackupFiles([drop]);

        var deleted = _store.DeleteAllExcept(new HashSet<Hash>());

        deleted.Should().Be(0);
        (await _store.HaveFile(drop.Hash)).Should().BeTrue();
    }

    [Fact]
    public async Task BackupFiles_ExistingFile_RefreshesMtime()
    {
        var e = Entry("reusado");
        await _store.BackupFiles([e]);
        File.SetLastWriteTimeUtc(_store.PathFor(e.Hash).ToString(), DateTime.UtcNow - TimeSpan.FromHours(2));

        await _store.BackupFiles([e]);

        (DateTime.UtcNow - File.GetLastWriteTimeUtc(_store.PathFor(e.Hash).ToString())).Should().BeLessThan(TimeSpan.FromMinutes(1));
        var deleted = _store.DeleteAllExcept(new HashSet<Hash>());
        deleted.Should().Be(0);
        (await _store.HaveFile(e.Hash)).Should().BeTrue();
    }

    [Fact]
    public void Gc_DoesNotDeleteTmpFilesInFlight()
    {
        // A temp file younger than the grace period belongs to a backup still running in another process step.
        var tmp = _store.PathFor(Hash.From(99)).Parent.Combine("0000000000000063.tmp-abc");
        tmp.Parent.CreateDirectory();
        tmp.Create().Dispose();

        _store.DeleteAllExcept(new HashSet<Hash>());

        tmp.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAllExcept_IgnoresForeignFilesAndDirectories()
    {
        var keep = Entry("vivo");
        await _store.BackupFiles([keep]);
        _store.GracePeriod = TimeSpan.Zero;

        // A top-level file that isn't a two-hex-char directory.
        var foreignTopLevel = StoreRoot.Combine("old.nx");
        await foreignTopLevel.WriteAllTextAsync("legacy archive");

        // An unrelated directory with a file inside.
        var foreignDirFile = StoreRoot.Combine("notes").Combine("readme.txt");
        foreignDirFile.Parent.CreateDirectory();
        await foreignDirFile.WriteAllTextAsync("not ours");

        // A 16-char name that isn't hex, sitting inside a legitimate two-hex-char directory.
        var prefix = keep.Hash.ToHex()[..2];
        var foreignInHexDir = StoreRoot.Combine(prefix).Combine("zzzzzzzzzzzzzzzz");
        await foreignInHexDir.WriteAllTextAsync("not a hash");

        var deleted = _store.DeleteAllExcept(new HashSet<Hash> { keep.Hash });

        deleted.Should().Be(0, "none of the foreign entries are owned by the store, and the live hash is kept");
        foreignTopLevel.FileExists.Should().BeTrue();
        foreignDirFile.FileExists.Should().BeTrue();
        foreignInHexDir.FileExists.Should().BeTrue();
        (await _store.HaveFile(keep.Hash)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAll_RemovesOnlyOwnedFiles()
    {
        var a = Entry("uno");
        var b = Entry("dos");
        await _store.BackupFiles([a, b]);

        var foreignDirFile = StoreRoot.Combine("notes").Combine("readme.txt");
        foreignDirFile.Parent.CreateDirectory();
        await foreignDirFile.WriteAllTextAsync("not ours");

        var prefix = a.Hash.ToHex()[..2];
        var foreignInHexDir = StoreRoot.Combine(prefix).Combine("zzzzzzzzzzzzzzzz");
        await foreignInHexDir.WriteAllTextAsync("not a hash");

        var deleted = _store.DeleteAll();

        deleted.Should().Be(2);
        (await _store.HaveFile(a.Hash)).Should().BeFalse();
        (await _store.HaveFile(b.Hash)).Should().BeFalse();
        foreignDirFile.FileExists.Should().BeTrue("DeleteAll must never touch foreign content");
        foreignInHexDir.FileExists.Should().BeTrue("DeleteAll must never touch foreign content, even inside an owned directory");
    }

    [Fact]
    public async Task TotalSize_ExcludesForeignFiles()
    {
        var e = Entry("conteo");
        await _store.BackupFiles([e]);
        var before = _store.TotalSize();

        var foreignTopLevel = StoreRoot.Combine("old.nx");
        await foreignTopLevel.WriteAllTextAsync("legacy archive, much bigger than the tracked file above");

        _store.TotalSize().Should().Be(before);
    }
}
