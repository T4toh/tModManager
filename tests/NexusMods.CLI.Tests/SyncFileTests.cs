using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NexusMods.SingleProcess;

namespace NexusMods.CLI.Tests;

public class SyncFileTests
{
    [Theory]
    [InlineData("1234 (NexusMods.App) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 987654 0 0", 987654UL)]
    [InlineData("1234 (we ird) (x) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 42 0 0", 42UL)]
    [InlineData("1234 (short) S 1 2", 0UL)]
    public void ParsesStartTimeAfterTheLastParenthesis(string stat, ulong expected)
    {
        SyncFile.ParseStartTime(stat).Should().Be(expected);
    }

    [Fact]
    public void OnlyOneProcessHoldsTheMainLock()
    {
        var path = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"sync-{Guid.NewGuid()}.sync");
        var lockPath = path.Parent.Combine(path.FileName + ".lock");
        try
        {
            var first = new SyncFile(NullLogger<SyncFile>.Instance, path);
            var second = new SyncFile(NullLogger<SyncFile>.Instance, path);

            first.TryLockMain().Should().BeTrue();
            first.TryLockMain().Should().BeTrue("it already holds it");
            second.TryLockMain().Should().BeFalse("the first launch holds it");

            // flock is per open file: another process (flock(1)) can't take it either
            using var flock = Process.Start("flock", ["-n", lockPath.ToString(), "true"]);
            flock.WaitForExit();
            flock.ExitCode.Should().Be(1);
        }
        finally
        {
            path.Delete();
            lockPath.Delete();
        }
    }

    [Fact]
    public void OnlyTheRecordedMainMatches()
    {
        var path = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"sync-{Guid.NewGuid()}.sync");
        using var other = Process.Start("sleep", "30");
        try
        {
            var syncFile = new SyncFile(NullLogger<SyncFile>.Instance, path);
            syncFile.IsRecordedMain(Process.GetCurrentProcess()).Should().BeFalse("nothing was recorded yet");

            syncFile.TrySetMain(port: 1234).Should().BeTrue();
            syncFile.IsRecordedMain(Process.GetCurrentProcess()).Should().BeTrue();
            syncFile.IsRecordedMain(other).Should().BeFalse("another PID");

            // The main died and its PID now belongs to another process: same PID, different start time
            using var array = new MultiProcessSharedArray(path, itemCount: 8);
            array.CompareAndSwap(0, array.Get(0), ((ulong)other.Id << 32) | 1234).Should().BeTrue();
            syncFile.IsRecordedMain(other).Should().BeFalse("the PID was reused");

            array.CompareAndSwap(1, array.Get(1), SyncFile.ProcessStartTime(other.Id)).Should().BeTrue();
            syncFile.IsRecordedMain(other).Should().BeTrue();
        }
        finally
        {
            other.Kill();
            path.Delete();
        }
    }
}
