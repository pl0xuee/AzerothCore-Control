namespace AzerothCoreControl.Core.Process;

/// <summary>
/// Launches a windowed program and waits for the user to close it. Unlike <see cref="CommandRunner"/> this
/// does not redirect stdio or hide the window — redirecting a GUI's streams gains nothing and hiding its
/// window would leave the caller waiting on something the user can't see.
/// </summary>
public static class InteractiveProcessRunner
{
    /// <summary>Start <paramref name="fileName"/> and complete once the user closes it.</summary>
    /// <exception cref="InvalidOperationException">The program could not be started (e.g. not installed).</exception>
    public static async Task RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = true,
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to start '{fileName}': {ex.Message}", ex);
        }

        // Cancelling abandons the wait but leaves the window open — it's the user's, not ours to kill.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}
