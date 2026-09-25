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
    // $0 = executable, $1 = PID to wait for, $2 = max waits of 0.1s, the rest = the executable's arguments.
    // If the old process is still alive after the cap, give up (exit) instead of starting a new process that would
    // race it: the reset marker stays, so the next manual launch still resets.
    private const string WaitThenExec =
        "pid=$1; max=$2; shift 2; i=0; while kill -0 \"$pid\" 2>/dev/null; do [ \"$i\" -ge \"$max\" ] && exit 1; sleep 0.1; i=$((i+1)); done; exec \"$0\" \"$@\"";

    /// <summary>About 60s of waiting for the old process to exit.</summary>
    private const int DefaultMaxWaits = 600;

    internal static ProcessStartInfo BuildStartInfo(string executable, int pid, IEnumerable<string> args, int maxWaits = DefaultMaxWaits)
    {
        // Arguments go through ArgumentList (argv), never through a command string, so paths are never re-parsed by the shell.
        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(WaitThenExec);
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(maxWaits.ToString(CultureInfo.InvariantCulture));
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
