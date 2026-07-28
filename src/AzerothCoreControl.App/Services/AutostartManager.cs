using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using AzerothCoreControl.Core.Services;
using Microsoft.Win32;

namespace AzerothCoreControl.App.Services;

/// <summary>
/// Toggles "launch this app when Windows starts" via a Task Scheduler job.
///
/// This used to write HKCU\Software\Microsoft\Windows\CurrentVersion\Run, which never worked: the app
/// manifest requests requireAdministrator, and Explorer runs Run-key entries at logon under the user's
/// filtered (standard) token with UAC consent prompts suppressed, so the launch was silently dropped.
/// The value wrote fine and read back fine — it simply never started anything. See
/// <see cref="AutostartTaskDefinition"/> for the task definition and the rest of the reasoning.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutostartManager
{
    private const string TaskName = AutostartTaskDefinition.TaskName;
    private const int TimeoutMs = 30_000;

    // The Run key this replaced. Still cleaned up on every toggle so old installs stop carrying a dead
    // "AzerothCoreControl" line in Task Manager's Startup tab.
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "AzerothCoreControl";

    /// <summary>
    /// True when the logon task is registered. Existence is the answer to "did we set autostart up" —
    /// someone disabling the task by hand in Task Scheduler is their own deliberate override.
    /// </summary>
    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"").ExitCode == 0;

    /// <summary>Register or remove the logon task.</summary>
    /// <exception cref="InvalidOperationException">The change could not be applied.</exception>
    public static void SetEnabled(bool enabled)
    {
        RemoveLegacyRunEntry();

        if (enabled)
            CreateTask();
        else
            DeleteTask();
    }

    private static void CreateTask()
    {
        // Environment.ProcessPath, not Assembly.Location: this ships as a single-file self-contained
        // exe, where Location is empty and only the host path is meaningful.
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("Could not determine this application's executable path.");

        var xml = AutostartTaskDefinition.BuildXml(exe, $@"{Environment.UserDomainName}\{Environment.UserName}");
        var xmlPath = Path.Combine(Path.GetTempPath(), $"accontrol-autostart-{Environment.ProcessId}.xml");
        try
        {
            // Encoding.Unicode (UTF-16LE + BOM): schtasks /Create /XML rejects UTF-8 files containing
            // non-ASCII, and the exe path runs through C:\Users\<name>, which routinely holds some.
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);

            var result = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (result.ExitCode != 0)
                throw new InvalidOperationException(Describe("register", result));
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temp file; nothing depends on it */ }
        }
    }

    private static void DeleteTask()
    {
        var result = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        // A non-zero exit usually just means it was never registered. Only complain if it is still there.
        if (result.ExitCode != 0 && IsEnabled())
            throw new InvalidOperationException(Describe("remove", result));
    }

    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best effort. A leftover value is inert — it never launched anything to begin with.
        }
    }

    private static string Describe(string verb, (int ExitCode, string StdOut, string StdErr) result)
    {
        var detail = result.StdErr.Trim();
        if (detail.Length == 0) detail = result.StdOut.Trim();
        if (detail.Length == 0) detail = $"schtasks.exe exited with code {result.ExitCode}.";
        return $"Could not {verb} the launch-on-boot task: {detail}";
    }

    private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Could not start schtasks.exe.");

        // Drain both pipes before waiting — a process that fills one while we block in WaitForExit
        // deadlocks the pair of us.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new InvalidOperationException("schtasks.exe did not respond within 30 seconds.");
        }
        process.WaitForExit(); // parameterless overload flushes the redirected streams

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
