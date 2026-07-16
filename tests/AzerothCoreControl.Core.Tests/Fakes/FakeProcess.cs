using AzerothCoreControl.Core.Process;

namespace AzerothCoreControl.Core.Tests.Fakes;

/// <summary>An in-memory <see cref="IProcessHandle"/> whose exit the test drives explicitly.</summary>
public sealed class FakeProcessHandle : IProcessHandle
{
    private readonly TaskCompletionSource<bool> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exitCode;
    private bool _exited;

    public FakeProcessHandle(ProcessStartSpec spec) => Spec = spec;

    public ProcessStartSpec Spec { get; }
    public List<string> StdinLines { get; } = new();

    /// <summary>When set, receiving a ".server shutdown" stdin line auto-exits with code 0 (mimics worldserver).</summary>
    public bool AutoExitOnShutdown { get; set; }

    public int Id => 1234;
    public bool HasExited => _exited;
    public int ExitCode => _exitCode;

    public event EventHandler? Exited;
    public event EventHandler<string>? OutputLine;
    public event EventHandler<string>? ErrorLine;

    public void EmitOutput(string line) => OutputLine?.Invoke(this, line);
    public void EmitError(string line) => ErrorLine?.Invoke(this, line);

    public void WriteStdin(string line)
    {
        StdinLines.Add(line);
        if (AutoExitOnShutdown && line.StartsWith(".server shutdown", StringComparison.OrdinalIgnoreCase))
            SimulateExit(0);
    }

    public void Kill()
    {
        if (!_exited)
            SimulateExit(-1);
    }

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_exited)
            return Task.FromResult(true);
        return WaitAsync(timeout, cancellationToken);
    }

    private async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var delay = Task.Delay(timeout, ct);
        var completed = await Task.WhenAny(_exitTcs.Task, delay).ConfigureAwait(false);
        return completed == _exitTcs.Task;
    }

    /// <summary>Drive the process to exit with a given code (raises <see cref="Exited"/>).</summary>
    public void SimulateExit(int code)
    {
        if (_exited) return;
        _exitCode = code;
        _exited = true;
        _exitTcs.TrySetResult(true);
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() { }
}

/// <summary>Records every launch so tests can inspect / drive the resulting handles.</summary>
public sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly SemaphoreSlim _launchSignal = new(0);
    private readonly object _gate = new();
    private readonly List<FakeProcessHandle> _launched = new();

    public bool AutoExitOnShutdown { get; set; }

    public IReadOnlyList<FakeProcessHandle> Launched
    {
        get { lock (_gate) return _launched.ToList(); }
    }

    public int LaunchCount
    {
        get { lock (_gate) return _launched.Count; }
    }

    public FakeProcessHandle Last
    {
        get { lock (_gate) return _launched[^1]; }
    }

    public IProcessHandle Launch(ProcessStartSpec spec)
    {
        var handle = new FakeProcessHandle(spec) { AutoExitOnShutdown = AutoExitOnShutdown };
        lock (_gate)
            _launched.Add(handle);
        _launchSignal.Release();
        return handle;
    }

    /// <summary>Await until at least <paramref name="count"/> launches have occurred.</summary>
    public async Task WaitForLaunchCountAsync(int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (LaunchCount < count)
            await _launchSignal.WaitAsync(cts.Token).ConfigureAwait(false);
    }
}
