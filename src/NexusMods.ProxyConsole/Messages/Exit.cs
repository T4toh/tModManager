using MemoryPack;

namespace NexusMods.ProxyConsole.Messages;

/// <summary>
/// Sent by the server once the command finished, carrying its exit code.
/// </summary>
[MemoryPackable]
public partial class Exit : IMessage
{
    /// <summary>
    /// The command's exit code.
    /// </summary>
    public required int Code { get; init; }
}
