using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GC;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;

namespace NexusMods.DataModel;

/// <inheritdoc />
public class GarbageCollectorRunner(LooseFileStore store, IConnection connection, ILogger<GarbageCollectorRunner> logger) : IGarbageCollectorRunner
{
    /// <inheritdoc />
    public void Run()
    {
        var live = LiveHashes.Collect(connection.Db);
        var deleted = store.DeleteAllExcept(live);
        logger.LogInformation("GC: {Deleted} archivos sin referencia borrados del store ({Live} vivos)", deleted, live.Count);
    }

    /// <inheritdoc />
    public Task RunAsync() => Task.Run(Run);

    /// <inheritdoc />
    public async Task RunWithMode(GarbageCollectorRunMode gcRunMode)
    {
        switch (gcRunMode)
        {
            case GarbageCollectorRunMode.RunSynchronously:
                Run();
                break;
            case GarbageCollectorRunMode.RunAsyncInBackground:
                RunAsync().FireAndForget(logger);
                break;
            case GarbageCollectorRunMode.RunAsynchronously:
                await RunAsync();
                break;
            case GarbageCollectorRunMode.DoNotRun:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(gcRunMode), gcRunMode, null);
        }
    }
}
