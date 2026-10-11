using System.Text;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Writes an <see cref="ISortOrderFile"/> as an intrinsic file. The loadout is the source of truth:
/// a file that differs from the rendered order is rewritten in the same apply, no External Change is
/// created (the game never writes this file). An empty order never creates a file out of nothing.
/// </summary>
public sealed class SortOrderIntrinsicFile(ISortOrderFile source) : IIntrinsicFile
{
    public GamePath Path => source.Path;

    public Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var bytes = Encoding.UTF8.GetBytes(source.Render(loadout, loadout.Db));
        return stream.WriteAsync(bytes, 0, bytes.Length);
    }

    public async Task<ReadOnlyMemory<byte>?> Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var diskText = await reader.ReadToEndAsync();
        var wanted = source.Render(loadout, loadout.Db);
        // Same text, or nothing to order and no file (Stream.Null reads as empty): leave the disk alone
        if (diskText == wanted) return null;
        return new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(wanted));
    }

    public bool IsUpToDate(string diskText, Loadout.ReadOnly loadout) => diskText == source.Render(loadout, loadout.Db);
}
