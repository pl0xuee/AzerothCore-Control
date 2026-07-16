namespace AzerothCoreControl.Core.Process;

/// <summary>
/// Abstraction over a running OS process so the supervisor can be unit-tested with a fake.
/// The real implementation wraps <see cref="System.Diagnostics.Process"/>.
/// </summary>
public interface IProcessHandle : IDisposable
{
    int Id { get; }
    bool HasExited { get; }

    /// <summary>Exit code — only meaningful once <see cref="HasExited"/> is true.</summary>
    int ExitCode { get; }

    /// <summary>Raised once when the process exits. Fires on a background thread.</summary>
    event EventHandler? Exited;

    /// <summary>A line written to the process's stdout.</summary>
    event EventHandler<string>? OutputLine;

    /// <summary>A line written to the process's stderr.</summary>
    event EventHandler<string>? ErrorLine;

    /// <summary>Write a line to the process's stdin (used for worldserver console commands).</summary>
    void WriteStdin(string line);

    /// <summary>Forcibly terminate the process tree.</summary>
    void Kill();

    /// <summary>Wait up to <paramref name="timeout"/> for exit. Returns false on timeout.</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Describes how to launch a process.</summary>
public sealed record ProcessStartSpec(
    string FileName,
    string Arguments = "",
    string? WorkingDirectory = null,
    bool RedirectStandardInput = true);

/// <summary>Factory for <see cref="IProcessHandle"/> — swapped for a fake in tests.</summary>
public interface IProcessLauncher
{
    IProcessHandle Launch(ProcessStartSpec spec);
}
