using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

public interface IIntrinsicFile
{
    /// <summary>
    /// The game path of this file.
    /// </summary>
    public GamePath Path { get; }

    /// <summary>
    /// Write the contents of this file to the stream.
    /// </summary>
    public Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree);

    /// <summary>
    /// Ingest the contents of the stream into the loadout. Returns the bytes that must end up on disk
    /// afterwards (base plus the loadout's entries, including any External Change created here), or
    /// null when the disk already matches. The synchronizer writes them in the same apply.
    /// </summary>
    public Task<ReadOnlyMemory<byte>?> Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx);
}
