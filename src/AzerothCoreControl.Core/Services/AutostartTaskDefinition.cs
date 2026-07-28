using System.Security;

namespace AzerothCoreControl.Core.Services;

/// <summary>
/// Builds the Task Scheduler job that launches the app at logon.
///
/// Deliberately NOT the HKCU\...\CurrentVersion\Run key, which is what this replaced. The app manifest
/// requests requireAdministrator; Explorer processes Run-key entries at logon using the user's filtered
/// (standard) token, and UAC consent prompts are suppressed during logon processing — so an entry
/// pointing at an elevation-requiring exe is silently dropped. Registration appeared to succeed, the
/// registry value read back correctly, and the app simply never started. A scheduled task running at
/// HighestAvailable is the supported way to auto-start an elevated app without a logon-time prompt.
///
/// This lives in Core, as pure string building, so it can be unit tested on the Linux dev box; the App
/// project owns the schtasks.exe invocation.
/// </summary>
public static class AutostartTaskDefinition
{
    /// <summary>Task Scheduler library name, in the root folder.</summary>
    public const string TaskName = "AzerothCoreControl";

    /// <summary>Argument the app parses to come up in the tray rather than showing its window.</summary>
    public const string StartMinimizedArgument = "--minimized";

    /// <summary>
    /// Produce the task XML for <c>schtasks /Create /XML</c>.
    /// </summary>
    /// <param name="exePath">Full path to the executable to launch.</param>
    /// <param name="userId">Account the task belongs to, as <c>DOMAIN\user</c>.</param>
    /// <param name="workingDirectory">Working directory; defaults to the executable's folder.</param>
    public static string BuildXml(string exePath, string userId, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("An executable path is required.", nameof(exePath));
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A user id is required.", nameof(userId));

        var trimmedExe = exePath.Trim();
        var workDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? WindowsDirectoryOf(trimmedExe)
            : workingDirectory.Trim();

        // Paths and account names are user-controlled and can legitimately contain & or ' — unescaped
        // they produce malformed XML that schtasks rejects outright.
        var exe = SecurityElement.Escape(trimmedExe);
        var user = SecurityElement.Escape(userId.Trim());
        var dir = SecurityElement.Escape(workDir);
        var args = SecurityElement.Escape(StartMinimizedArgument);

        // encoding="UTF-16" must match how the file is written: schtasks /Create /XML rejects UTF-8
        // files containing non-ASCII, which a path under C:\Users\<name> can easily hold.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts AzerothCore Control in the tray when {user} signs in.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{args}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>
    /// Directory portion of a Windows path, without Path.GetDirectoryName. These are always Windows
    /// paths, but this assembly is unit tested on the Linux dev box, where GetDirectoryName does not
    /// treat '\' as a separator and hands back "" for every one of them.
    /// </summary>
    private static string WindowsDirectoryOf(string path)
    {
        var cut = path.LastIndexOfAny(Separators);
        if (cut <= 0)
            return "";
        // Keep the separator at a drive root ("C:\app.exe" -> "C:\"), drop it anywhere else.
        return path[cut - 1] == ':' ? path[..(cut + 1)] : path[..cut];
    }
}
