using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.SingleProcess;

/// <summary>
/// A shared file that is used to synchronize the main process and client processes.
/// </summary>
public class SyncFile
{
    private readonly MultiProcessSharedArray _sharedArray;
    private readonly ILogger<SyncFile> _logger;
    private readonly AbsolutePath _lockPath;
    private FileStream? _mainLock;

    /// <summary>
    /// DI constructor
    /// </summary>
    public SyncFile(ILogger<SyncFile> logger, ISettingsManager settingsManager)
        : this(logger, settingsManager.Get<CliSettings>().SyncFile) { }

    internal SyncFile(ILogger<SyncFile> logger, AbsolutePath path)
    {
        _logger = logger;
        _logger.LogDebug("Using Sync File: {SyncFile}", path);
        // Slot 0: PID << 32 | port. Slot 1: the main's start time (see ProcessStartTime)
        _sharedArray = new MultiProcessSharedArray(path, itemCount: 8);
        _lockPath = path.Parent.Combine(path.FileName + ".lock");
    }

    /// <summary>
    /// Takes the exclusive lock that only the main process holds, until this process exits. Returns false if
    /// another process holds it. Two launches inside the startup window both find no main in the sync file
    /// (it's written only after migrations); the lock lets only one of them run the reset and migrations.
    /// </summary>
    public bool TryLockMain()
    {
        if (_mainLock is not null) return true;
        try
        {
            // FileShare.None is an flock(LOCK_EX | LOCK_NB) on Linux, released by the kernel when the process dies
            _mainLock = new FileStream(_lockPath.ToString(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
    
    /// <summary>
    /// True if the main process is running
    /// </summary>
    public bool IsMainRunning => GetSyncInfo().Process is not null;
    
    /// <summary>
    /// Returns the current process and port that's stored in the sync file
    /// </summary>
    /// <returns></returns>
    public (Process? Process, int Port) GetSyncInfo()
    {
        var rawValue = _sharedArray.Get(0);
        var processId = (int)(rawValue >> 32);
        var port = (int) rawValue;
        
        
        _logger.LogDebug("Sync file contents: {Port}, {ProcessId}", port, processId);

        if (processId == 0) return (null, port);
        var process = GetProcessById(processId);
        return (process, port);
    }

    /// <summary>
    /// Flags this process as the main process, returns false if another process is already the main process
    /// </summary>
    public bool TrySetMain(int port)
    {
        var newProcessId = Environment.ProcessId;
        var newContents = ((ulong)newProcessId << 32) | (uint) port;


        var currentRawValue = _sharedArray.Get(0);
        var currentProcessId = (int)(currentRawValue >> 32);

        var process = GetProcessById(currentProcessId);
        if (process is not null && process.Id != newProcessId)
            return false;

        if (!_sharedArray.CompareAndSwap(0, currentRawValue, newContents))
            return false;

        _sharedArray.CompareAndSwap(1, _sharedArray.Get(1), ProcessStartTime(newProcessId));
        return true;
    }

    /// <summary>
    /// True only if <paramref name="process"/> is the main that wrote the sync file: same PID and same
    /// start time. A PID outlives a crash and gets reused, and the process name doesn't tell (under
    /// `dotnet NexusMods.App.dll` every dotnet process is called `dotnet`). False when the start time is
    /// unknown (file written by an older version, or /proc unreadable).
    /// </summary>
    public bool IsRecordedMain(Process process)
    {
        var recorded = _sharedArray.Get(1);
        return recorded != 0
            && (int)(_sharedArray.Get(0) >> 32) == process.Id
            && ProcessStartTime(process.Id) == recorded;
    }

    /// <summary>
    /// Start time of <paramref name="pid"/> in clock ticks since boot (field 22 of /proc/[pid]/stat), or 0.
    /// The sync file lives in /run/user, which doesn't survive a reboot, so ticks since boot are enough.
    /// </summary>
    internal static ulong ProcessStartTime(int pid)
    {
        try
        {
            return ParseStartTime(File.ReadAllText($"/proc/{pid}/stat"));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal static ulong ParseStartTime(string stat)
    {
        // "pid (comm) state ppid ...": comm may hold spaces and ')', so count from the last ')'
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return fields.Length > 19 && ulong.TryParse(fields[19], out var ticks) ? ticks : 0;
    }
    
    /// <summary>
    /// Returns the process with the given id, or null if it doesn't exist
    /// </summary>
    private static Process? GetProcessById(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return process.Id == 0 ? null : process;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

