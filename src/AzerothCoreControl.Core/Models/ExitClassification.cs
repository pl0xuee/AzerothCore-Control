namespace AzerothCoreControl.Core.Models;

/// <summary>
/// How the supervisor interprets a worldserver/authserver process exit.
/// Based on AzerothCore's exit-code protocol (see https://www.azerothcore.org/wiki/exitcodes):
///   0 = clean shutdown   → stay down
///   1 = restart request  → restart immediately
///   anything else / abrupt death = crash → restart with backoff (until the breaker trips)
/// </summary>
public enum ExitClassification
{
    /// <summary>Exit code 0 — the server was told to shut down. Do not restart.</summary>
    CleanShutdown,

    /// <summary>Exit code 1 — the server asked to be restarted (e.g. <c>.server restart</c>).</summary>
    RestartRequested,

    /// <summary>Any other exit code, or the process vanished — treat as a crash.</summary>
    Crash,
}

public static class ExitCodePolicy
{
    /// <summary>AzerothCore SHUTDOWN_EXIT_CODE.</summary>
    public const int ShutdownExitCode = 0;

    /// <summary>AzerothCore RESTART_EXIT_CODE.</summary>
    public const int RestartExitCode = 1;

    public static ExitClassification Classify(int exitCode) => exitCode switch
    {
        ShutdownExitCode => ExitClassification.CleanShutdown,
        RestartExitCode => ExitClassification.RestartRequested,
        _ => ExitClassification.Crash,
    };
}
