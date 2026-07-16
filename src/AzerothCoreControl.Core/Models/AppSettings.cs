namespace AzerothCoreControl.Core.Models;

/// <summary>
/// All user-configurable settings, persisted as JSON at
/// <c>%AppData%\AzerothCoreControl\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Directory holding the live authserver.exe / worldserver.exe and their .conf files.</summary>
    public string? RunDirectory { get; set; }

    /// <summary>Root of the AzerothCore source checkout (contains the <c>modules/</c> folder). Optional.</summary>
    public string? SourceDirectory { get; set; }

    /// <summary>CMake build directory used for recompiles. Optional.</summary>
    public string? BuildDirectory { get; set; }

    /// <summary>Directory that receives freshly built binaries — usually equals <see cref="RunDirectory"/>.</summary>
    public string? DeployDirectory { get; set; }

    public WatchdogSettings Watchdog { get; set; } = new();
    public MySqlSettings MySql { get; set; } = new();
    public BuildSettings Build { get; set; } = new();
    public GitHubSettings GitHub { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();

    /// <summary>Restart/backup schedules (cron-like via simple time-of-day + weekday mask).</summary>
    public List<ScheduledJob> Schedules { get; set; } = new();

    /// <summary>How often to poll GitHub for module updates.</summary>
    public TimeSpan ModuleCheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Automatically check GitHub for a newer version of this app (on launch + on interval).</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>When an app update is found, download and install it automatically (in-place, then relaunch).</summary>
    public bool AutoInstallUpdates { get; set; }

    /// <summary>How often to check for app updates when <see cref="AutoCheckForUpdates"/> is on.</summary>
    public TimeSpan AppUpdateCheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Launch this app when Windows starts.</summary>
    public bool LaunchOnBoot { get; set; }

    /// <summary>Automatically start both servers when the app launches.</summary>
    public bool AutoStartServers { get; set; }

    /// <summary>Start minimized to the system tray instead of showing the (maximized) window on launch.</summary>
    public bool StartMinimizedToTray { get; set; }
}

public sealed class WatchdogSettings
{
    /// <summary>Auto-restart authserver/worldserver on crash.</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>First restart delay after a crash; doubles each consecutive crash up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>If the server crashes this many times inside <see cref="CrashWindow"/>, stop auto-restarting and alert.</summary>
    public int CrashLoopThreshold { get; set; } = 5;

    public TimeSpan CrashWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Seconds of in-game warning (<c>.server shutdown &lt;n&gt;</c>) before a graceful stop.</summary>
    public int GracefulShutdownSeconds { get; set; } = 30;
}

public sealed class MySqlSettings
{
    /// <summary>Windows service name for the MySQL/MariaDB instance (e.g. "MySQL80").</summary>
    public string? ServiceName { get; set; }

    /// <summary>Require MySQL to be running before launching the servers.</summary>
    public bool RequireForStart { get; set; } = true;

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string Username { get; set; } = "root";
    public string? Password { get; set; }

    /// <summary>Database names to back up.</summary>
    public List<string> Databases { get; set; } = new() { "acore_auth", "acore_characters", "acore_world" };
}

public sealed class BuildSettings
{
    /// <summary>Path to cmake.exe (or just "cmake" if on PATH).</summary>
    public string CMakePath { get; set; } = "cmake";

    /// <summary>Build configuration passed to cmake --build (Release/RelWithDebInfo/Debug).</summary>
    public string Configuration { get; set; } = "Release";

    /// <summary>Parallel build job count (0 = let the tool decide).</summary>
    public int Parallelism { get; set; }
}

public sealed class GitHubSettings
{
    /// <summary>Optional personal access token — lifts the 60/hr anonymous API rate limit to 5000/hr.</summary>
    public string? Token { get; set; }

    /// <summary>"owner/repo" of this control app's own GitHub repo, used by the self-update check.</summary>
    public string? AppReleaseRepo { get; set; } = "pl0xuee/AzerothCore-Control";
}

public sealed class NotificationSettings
{
    public bool ToastEnabled { get; set; } = true;

    public string? DiscordWebhookUrl { get; set; }

    public bool EmailEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? EmailFrom { get; set; }
    public string? EmailTo { get; set; }
}

public sealed class BackupSettings
{
    /// <summary>Path to mysqldump.exe (or "mysqldump" if on PATH).</summary>
    public string MysqlDumpPath { get; set; } = "mysqldump";

    /// <summary>Directory where backup archives are written.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Keep at most this many backups; older ones are pruned. 0 = keep all.</summary>
    public int RetentionCount { get; set; } = 14;

    /// <summary>Take a backup automatically before applying updates.</summary>
    public bool BackupBeforeUpdate { get; set; } = true;
}

public enum ScheduledJobKind
{
    Restart,
    Backup,
}

/// <summary>A daily/weekly job fired at a local time-of-day.</summary>
public sealed class ScheduledJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ScheduledJobKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Local time-of-day to fire.</summary>
    public TimeSpan TimeOfDay { get; set; }

    /// <summary>Days the job runs. Empty = every day.</summary>
    public List<DayOfWeek> Days { get; set; } = new();
}
