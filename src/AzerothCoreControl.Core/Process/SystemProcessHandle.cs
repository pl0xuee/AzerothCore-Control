using System.Diagnostics;

namespace AzerothCoreControl.Core.Process;

/// <summary>Real <see cref="IProcessHandle"/> backed by <see cref="System.Diagnostics.Process"/>.</summary>
public sealed class SystemProcessHandle : IProcessHandle
{
    private readonly System.Diagnostics.Process _process;
    private int _disposed;

    public event EventHandler? Exited;
    public event EventHandler<string>? OutputLine;
    public event EventHandler<string>? ErrorLine;

    private SystemProcessHandle(System.Diagnostics.Process process)
    {
        _process = process;
    }

    public static SystemProcessHandle Start(ProcessStartSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory ?? Path.GetDirectoryName(spec.FileName) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = spec.RedirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var handle = new SystemProcessHandle(process);

        process.OutputDataReceived += (_, e) => { if (e.Data != null) handle.OutputLine?.Invoke(handle, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) handle.ErrorLine?.Invoke(handle, e.Data); };
        process.Exited += (_, _) => handle.Exited?.Invoke(handle, EventArgs.Empty);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{spec.FileName}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return handle;
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public void WriteStdin(string line)
    {
        if (_process.HasExited)
            return;
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // timed out
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _process.Dispose();
    }
}

/// <summary>Default launcher used at runtime.</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public IProcessHandle Launch(ProcessStartSpec spec) => SystemProcessHandle.Start(spec);
}
