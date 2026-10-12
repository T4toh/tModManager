using System.Text;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class SortOrderIntrinsicFileTests
{
    private sealed class FakeOrder(string text) : ISortOrderFile
    {
        public string Text { get; set; } = text;
        public IEnumerable<GameId> GameIds => [GameId.From("Stub")];
        public GamePath Path => new(LocationId.Game, "order.txt");
        public string Render(Loadout.ReadOnly loadout, IDb db) => Text;
    }

    private static Stream Disk(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Write_IsTheRenderedText()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder("a\r\nb"));
        using var stream = new MemoryStream();

        await file.Write(stream, default, new Dictionary<GamePath, SyncNode>());

        Encoding.UTF8.GetString(stream.ToArray()).Should().Be("a\r\nb");
    }

    [Fact]
    public async Task Ingest_DiskMatches_ReturnsNull()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder("a\r\nb"));

        var rewrite = await file.Ingest(Disk("a\r\nb"), default, new Dictionary<GamePath, SyncNode>(), null!);

        rewrite.Should().BeNull();
    }

    [Fact]
    public async Task Ingest_DiskDiffers_ReturnsTheRenderedText_TheLoadoutWins()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder("a\r\nb"));

        var rewrite = await file.Ingest(Disk("b\na"), default, new Dictionary<GamePath, SyncNode>(), null!);

        rewrite.Should().NotBeNull();
        Encoding.UTF8.GetString(rewrite!.Value.ToArray()).Should().Be("a\r\nb");
    }

    [Fact]
    public async Task Ingest_NothingToOrderAndNoFile_ReturnsNull()
    {
        // An empty order never creates an empty file out of nothing
        var file = new SortOrderIntrinsicFile(new FakeOrder(string.Empty));

        var rewrite = await file.Ingest(Stream.Null, default, new Dictionary<GamePath, SyncNode>(), null!);

        rewrite.Should().BeNull();
    }

    [Fact]
    public async Task Ingest_NothingToOrder_LeavesAForeignFileAlone()
    {
        // Another manager's file with nothing of ours to write: not ours to empty
        var file = new SortOrderIntrinsicFile(new FakeOrder(string.Empty));

        var rewrite = await file.Ingest(Disk("vortex_mod"), default, new Dictionary<GamePath, SyncNode>(), null!);

        rewrite.Should().BeNull();
        file.IsUpToDate("vortex_mod", default).Should().BeTrue("nothing to write means nothing pending");
    }

    [Fact]
    public void IsUpToDate_ComparesTextExactly()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder("a\r\nb"));

        file.IsUpToDate("a\r\nb", default).Should().BeTrue();
        file.IsUpToDate("a\nb", default).Should().BeFalse("REDmod needs CRLF: a hand edit with LF is pending");
        file.IsUpToDate(string.Empty, default).Should().BeFalse();
    }
}
