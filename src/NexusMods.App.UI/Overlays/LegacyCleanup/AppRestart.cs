using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using NexusMods.Sdk;

namespace NexusMods.App.UI.Overlays;

/// <summary>
/// Restarts the app so that the new process only starts once this one has fully exited (and released the database).
/// </summary>
public static class AppRestart
{
    // $0 = executable, $1 = PID to wait for, the rest = the executable's arguments.
    // ponytail: gives up waiting after ~60s (e.g. an unreaped zombie); the new process then just defers to Program's
    // "another main is alive" gate, and the reset marker stays for the next launch.
    private const string WaitThenExec =
        "pid=$1; shift; i=0; while kill -0 \"$pid\" 2>/dev/null && [ \"$i\" -lt 600 ]; do sleep 0.1; i=$((i+1)); done; exec \"$0\" \"$@\"";

    internal static ProcessStartInfo BuildStartInfo(string executable, int pid, IEnumerable<string> args)
    {
        // Arguments go through ArgumentList (argv), never through a command string, so paths are never re-parsed by the shell.
        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(WaitThenExec);
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    /// <summary>Starts a waiter that relaunches the app after this process exits, then shuts the app down.</summary>
    public static void RestartAfterExit(IOSInterop osInterop)
    {
        // Prefers $APPIMAGE over the path inside the (soon unmounted) AppImage mount.
        osInterop.GetRunningExecutablePath(out var executable);
        var startInfo = BuildStartInfo(executable, Environment.ProcessId, Environment.GetCommandLineArgs().Skip(1));
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo iniciar el proceso de reinicio");
        Shutdown();
    }

    /// <summary>Shuts the app down through the normal Avalonia lifetime (so the host and the database are disposed).</summary>
    public static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Environment.Exit(0);
    }
}
