using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.App.UI.Overlays;
using NexusMods.DataModel.Storage;
using NexusMods.Paths;
using NexusMods.Sdk;
using NSubstitute;
using R3;

namespace NexusMods.UI.Tests.Overlays;

public class LegacyCleanupOverlayTests
{
    [Fact]
    public void RestartStartInfo_PassesEverythingAsSeparateArguments()
    {
        var startInfo = AppRestart.BuildStartInfo("/opt/my app/tModManager", 1234, ["as-main-ui", "a b"]);

        startInfo.FileName.Should().Be("/bin/sh");
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.Arguments.Should().BeEmpty();
        startInfo.ArgumentList.Should().HaveCount(7);
        startInfo.ArgumentList[0].Should().Be("-c");
        startInfo.ArgumentList.Skip(2).Should().Equal("/opt/my app/tModManager", "1234", "600", "as-main-ui", "a b");
    }

    [Fact]
    public void RestartScript_ExecsTheExecutableWithTheArgumentsVerbatim()
    {
        using var tempDir = new TempDir();
        var exe = Path.Combine(tempDir.Path, "dir with space", "prin tf");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.CreateSymbolicLink(exe, "/usr/bin/printf");

        string[] args = ["%s|", "with space", "$(echo pwned)", "it's \"quoted\"", ";echo injected", "*"];
        var output = RunToEnd(AppRestart.BuildStartInfo(exe, ExitedPid(), args));

        output.Should().Be("with space|$(echo pwned)|it's \"quoted\"|;echo injected|*|");
    }

    [Fact]
    public void RestartScript_WaitsForTheOldProcessToExit()
    {
        using var old = Process.Start("sleep", "1");
        var startInfo = AppRestart.BuildStartInfo("/bin/echo", old.Id, ["started"]);
        startInfo.RedirectStandardOutput = true;
        using var waiter = Process.Start(startInfo)!;

        waiter.WaitForExit(TimeSpan.FromMilliseconds(400)).Should().BeFalse("the old process is still running");
        old.HasExited.Should().BeFalse();

        waiter.WaitForExit(TimeSpan.FromSeconds(20)).Should().BeTrue();
        old.HasExited.Should().BeTrue();
        waiter.StandardOutput.ReadToEnd().Should().Be("started\n");
    }

    [Fact]
    public void RestartScript_GivesUpInsteadOfRacingAnOldProcessThatNeverExits()
    {
        using var old = Process.Start("sleep", "30");
        try
        {
            var startInfo = AppRestart.BuildStartInfo("/bin/echo", old.Id, ["started"], maxWaits: 3);
            startInfo.RedirectStandardOutput = true;
            using var waiter = Process.Start(startInfo)!;

            waiter.WaitForExit(TimeSpan.FromSeconds(20)).Should().BeTrue();
            waiter.ExitCode.Should().Be(1);
            waiter.StandardOutput.ReadToEnd().Should().BeEmpty("the new process must never start while the old one lives");
            old.HasExited.Should().BeFalse();
        }
        finally
        {
            old.Kill();
        }
    }

    [Fact]
    public void FindSteamLibraryRoot_WalksUpToTheFolderWithSteamapps()
    {
        using var tempDir = new TempDir();
        var library = FileSystem.Shared.FromUnsanitizedFullPath(tempDir.Path);
        var game = library.Combine("steamapps").Combine("common").Combine("Cyberpunk 2077");
        game.CreateDirectory();

        LegacyCleanupOverlayViewModel.FindSteamLibraryRoot(game).Should().Be(library);
        LegacyCleanupOverlayViewModel.FindSteamLibraryRoot(library.Combine("steamapps")).Should().Be(library);
    }

    [Fact]
    public void FindSteamLibraryRoot_ReturnsNullWithoutSteamapps()
    {
        using var tempDir = new TempDir();
        var game = FileSystem.Shared.FromUnsanitizedFullPath(tempDir.Path).Combine("Games").Combine("Cyberpunk 2077");
        game.CreateDirectory();

        LegacyCleanupOverlayViewModel.FindSteamLibraryRoot(game).Should().BeNull();
    }

    [Fact]
    public void Wizard_RunsEachStepInOrderAndRestartsAtTheEnd()
    {
        var storage = Substitute.For<IStorageAnalyzer>();
        storage.GetLegacyDownloadsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult((3, Size.From(2048))));
        var root = FileSystem.Shared.FromUnsanitizedFullPath("/games/SteamLibrary");
        var restarts = 0;
        var vm = CreateVm(storage, () => root, () => restarts++);

        vm.Step.Value.Should().Be(1);
        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(2);
        vm.LegacyDownloadsText.Value.Should().StartWith("3 archivos (");
        storage.DidNotReceive().MoveLegacyDownloadsAsync(Arg.Any<CancellationToken>());

        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(3);
        storage.Received(1).MoveLegacyDownloadsAsync(Arg.Any<CancellationToken>());
        storage.DidNotReceive().RunDeepCleanWithoutSyncOnAllLoadoutsAsync(Arg.Any<CancellationToken>());

        vm.DeleteProtonPrefix.Value = true;
        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(4);
        storage.Received(1).RunDeepCleanWithoutSyncOnAllLoadoutsAsync(Arg.Any<CancellationToken>());
        storage.Received(1).DeleteProtonPrefixAsync(root, Arg.Any<CancellationToken>());
        restarts.Should().Be(0);

        vm.CommandNext.Execute(Unit.Default);
        restarts.Should().Be(1);
        vm.Message.Value.Should().BeEmpty();
        vm.IsBusy.Value.Should().BeTrue("the buttons stay disabled while the app shuts down");
    }

    [Fact]
    public void Wizard_StaysOnAFailedStepUntilItSucceeds()
    {
        var storage = Substitute.For<IStorageAnalyzer>();
        storage.GetLegacyDownloadsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult((0, Size.Zero)));
        storage.RunDeepCleanWithoutSyncOnAllLoadoutsAsync(Arg.Any<CancellationToken>()).Returns(
            _ => throw new IOException("disco lleno"),
            _ => Task.CompletedTask
        );
        var restarts = 0;
        var vm = CreateVm(storage, () => null, () => restarts++);

        vm.CommandNext.Execute(Unit.Default);
        vm.LegacyDownloadsText.Value.Should().Be("No hay descargas viejas para rescatar.");
        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(3);

        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(3);
        vm.Message.Value.Should().Contain("disco lleno");
        vm.IsBusy.Value.Should().BeFalse();
        restarts.Should().Be(0);

        vm.DeleteProtonPrefix.Value = true;
        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(4);
        vm.Message.Value.Should().Contain("prefix de Proton no se borró");
        storage.DidNotReceiveWithAnyArgs().DeleteProtonPrefixAsync(default);
        restarts.Should().Be(0);
    }

    [Fact]
    public void Wizard_AfterAFailedDeepClean_CanContinueToTheResetWithoutCleaning()
    {
        var storage = Substitute.For<IStorageAnalyzer>();
        storage.GetLegacyDownloadsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult((0, Size.Zero)));
        storage.RunDeepCleanWithoutSyncOnAllLoadoutsAsync(Arg.Any<CancellationToken>()).Returns(_ => throw new IOException("MissingArchiveException"));
        var restarts = 0;
        var vm = CreateVm(storage, () => null, () => restarts++);
        vm.CommandNext.Execute(Unit.Default);
        vm.CommandNext.Execute(Unit.Default);

        vm.CanSkipCleanup.Value.Should().BeFalse();
        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(3);
        vm.CanSkipCleanup.Value.Should().BeTrue();

        vm.CommandSkipCleanup.Execute(Unit.Default);
        vm.Step.Value.Should().Be(4);
        vm.CleanupSkipped.Value.Should().BeTrue();
        vm.Message.Value.Should().BeEmpty();

        vm.CommandNext.Execute(Unit.Default);
        restarts.Should().Be(1);
    }

    [Fact]
    public void Wizard_RetryAfterAFailedProtonPrefix_DoesNotRunDeepCleanAgain()
    {
        var storage = Substitute.For<IStorageAnalyzer>();
        storage.GetLegacyDownloadsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult((0, Size.Zero)));
        storage.DeleteProtonPrefixAsync(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>()).Returns(
            _ => throw new UnauthorizedAccessException("permiso denegado"),
            _ => Task.CompletedTask
        );
        var root = FileSystem.Shared.FromUnsanitizedFullPath("/games/SteamLibrary");
        var vm = CreateVm(storage, () => root, () => { });
        vm.CommandNext.Execute(Unit.Default);
        vm.CommandNext.Execute(Unit.Default);
        vm.DeleteProtonPrefix.Value = true;

        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(3);
        vm.Message.Value.Should().Contain("permiso denegado");

        vm.CommandNext.Execute(Unit.Default);
        vm.Step.Value.Should().Be(4);
        vm.CleanupSkipped.Value.Should().BeFalse();
        storage.Received(1).RunDeepCleanWithoutSyncOnAllLoadoutsAsync(Arg.Any<CancellationToken>());
        storage.Received(2).DeleteProtonPrefixAsync(root, Arg.Any<CancellationToken>());
        storage.DidNotReceive().RunDeepCleanOnAllLoadoutsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void VerifySteam_ShowsAnErrorWhenSteamCantBeOpened()
    {
        var os = Substitute.For<IOSInterop>();
        os.When(x => x.OpenUri(Arg.Any<Uri>())).Do(_ => throw new InvalidOperationException("sin portal"));
        var vm = new LegacyCleanupOverlayViewModel(Substitute.For<IStorageAnalyzer>(), os, NullLogger.Instance, () => null, () => { }, () => { });

        vm.CommandVerifySteam.Execute(Unit.Default);

        os.Received(1).OpenUri(new Uri("steam://validate/1091500"));
        vm.Message.Value.Should().Contain("sin portal");
    }

    private static LegacyCleanupOverlayViewModel CreateVm(IStorageAnalyzer storage, Func<AbsolutePath?> steamLibraryRoot, Action restart) => new(
        storage,
        Substitute.For<IOSInterop>(),
        NullLogger.Instance,
        steamLibraryRoot,
        restart,
        quit: () => throw new InvalidOperationException("quit")
    );

    private static int ExitedPid()
    {
        using var process = Process.Start("/bin/true");
        process.WaitForExit();
        return process.Id;
    }

    private static string RunToEnd(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardOutput = true;
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(20)).Should().BeTrue();
        return output;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("legacy-cleanup-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
