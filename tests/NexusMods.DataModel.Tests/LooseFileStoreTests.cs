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
}
