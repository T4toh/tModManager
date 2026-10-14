using System.Runtime.CompilerServices;

namespace NexusMods.Tests;

/// <summary>
/// Settings, configs, logs, the hash database, temp files and the single-instance sync file all default to the XDG
/// folders of whoever runs the tests. Runs before any code of the test assembly and points them at a throwaway
/// folder, so a test run never reads or writes the user's real tModManager data (it did: #32, #33, and the logs,
/// configs and hash database until this). Left in the system temp folder on purpose: nothing here is worth a
/// recursive delete at exit.
/// </summary>
internal static class TestIsolation
{
    [ModuleInitializer]
    internal static void RedirectXdgFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tmm-tests-{Environment.ProcessId}");
        foreach (var (variable, folder) in new[]
                 {
                     ("XDG_DATA_HOME", "data"), ("XDG_STATE_HOME", "state"), ("XDG_CONFIG_HOME", "config"),
                     ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "runtime"),
                 })
        {
            var path = Path.Combine(root, folder);
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(variable, path);
        }
    }
}
