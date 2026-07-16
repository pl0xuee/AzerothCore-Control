namespace AzerothCoreControl.Core.Models;

/// <summary>
/// Lifecycle states of a supervised server process. The supervisor is a small state machine:
/// Stopped → Starting → Running → (Stopped | Crashed | Restarting) → ...
/// </summary>
public enum ServerState
{
    /// <summary>Not running and not scheduled to run (clean/user stop).</summary>
    Stopped,

    /// <summary>Process spawn in progress.</summary>
    Starting,

    /// <summary>Process is alive.</summary>
    Running,

    /// <summary>Waiting out a backoff delay before an automatic restart.</summary>
    Restarting,

    /// <summary>Died unexpectedly and the crash-loop breaker has tripped — will NOT auto-restart.</summary>
    Crashed,
}
