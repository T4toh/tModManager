using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GC;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class GarbageCollectorTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GarbageCollectorTests>(helper)
{
    [Fact]
    public async Task DeletingLoadout_RemovesOnlyItsUnsharedFiles()
    {
        var store = ServiceProvider.GetRequiredService<IFileStore>();
        ((LooseFileStore)store).GracePeriod = TimeSpan.Zero;
        var gc = ServiceProvider.GetRequiredService<IGarbageCollectorRunner>();
        var loadout1 = await CreateLoadout();
        var loadout2 = await CreateLoadout();

        List<Hash> shared1, shared2, only1, only2;
        using (var tx = Connection.BeginTransaction())
        {
            shared1 = await AddModAsync(tx, new RelativePath[] { "shared1.txt", "shared2.txt" }, loadout1, "Shared");
            shared2 = await AddModAsync(tx, new RelativePath[] { "shared1.txt", "shared2.txt" }, loadout2, "Shared");
            only1 = await AddModAsync(tx, new RelativePath[] { "l1_a.txt", "l1_b.txt" }, loadout1, "L1");
            only2 = await AddModAsync(tx, new RelativePath[] { "l2_a.txt", "l2_b.txt" }, loadout2, "L2");
            await tx.Commit();
        }

        gc.Run();
        foreach (var h in shared1.Concat(shared2).Concat(only1).Concat(only2))
            (await store.HaveFile(h)).Should().BeTrue("everything is referenced");

        await DeleteLoadoutAsync(loadout2, GarbageCollectorRunMode.RunSynchronously);

        foreach (var h in shared1.Concat(only1))
            (await store.HaveFile(h)).Should().BeTrue();
        foreach (var h in only2)
            (await store.HaveFile(h)).Should().BeFalse();
    }
}
