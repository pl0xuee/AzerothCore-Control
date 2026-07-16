using System.Runtime.Versioning;

namespace AzerothCoreControl.Core.Process;

/// <summary>
/// Kills stale/orphaned instances of a server executable before (re)starting it. AzerothCore runs a single
/// authserver/worldserver; a leftover instance holds its port (e.g. 3724), so a fresh start fails with
/// "Could not bind ... only one usage of each socket address is normally permitted". Cleaning up first
/// avoids that.
/// </summary>
public static class ProcessCleanup
{
    /// <summary>
    /// Terminate any running process launched from exactly <paramref name="executablePath"/>. Only matches
    /// that exact binary path, so unrelated processes are never touched. Returns the number killed.
    /// </summary>
    public static int KillStaleInstances(string executablePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath))
            return 0;
        return KillWindows(executablePath);
    }

    [SupportedOSPlatform("windows")]
    private static int KillWindows(string executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrEmpty(name))
            return 0;

        var killed = 0;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
        {
            try
            {
                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch { /* access denied / exited between enumerate and read — skip */ }

                if (path != null && string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    killed++;
                }
            }
            catch
            {
                // Already gone or can't be killed — ignore; the subsequent bind attempt will report if it's stuck.
            }
            finally
            {
                process.Dispose();
            }
        }
        return killed;
    }
}
