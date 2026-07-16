using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public sealed record BuildOutcome(bool Success, int ExitCode, string? BinaryOutputDir);

/// <summary>
/// Automates recompiling AzerothCore via CMake. Assumes the build directory was already configured
/// once by the user (the normal Windows install flow); this runs an incremental
/// <c>cmake --build &lt;buildDir&gt; --config &lt;cfg&gt; --target authserver worldserver</c>.
/// If the build directory has no CMake cache, it runs a configure step first.
/// </summary>
public sealed class BuildService
{
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;

    public BuildService(Func<AppSettings> settings, ILogger<BuildService>? logger = null)
    {
        _settings = settings;
        _log = logger ?? NullLogger<BuildService>.Instance;
    }

    /// <summary>Run the build, streaming compiler output line-by-line to <paramref name="onOutputLine"/>.</summary>
    public async Task<BuildOutcome> BuildAsync(Action<string>? onOutputLine = null, CancellationToken cancellationToken = default)
    {
        var s = _settings();
        var build = s.Build;
        var buildDir = s.BuildDirectory;
        var sourceDir = s.SourceDirectory;

        if (string.IsNullOrWhiteSpace(buildDir))
            throw new InvalidOperationException("Build directory is not configured.");

        Directory.CreateDirectory(buildDir);

        // Configure if the build tree hasn't been generated yet.
        if (!File.Exists(Path.Combine(buildDir, "CMakeCache.txt")))
        {
            if (string.IsNullOrWhiteSpace(sourceDir))
                throw new InvalidOperationException("Source directory is required for the initial CMake configure.");

            onOutputLine?.Invoke($"[configure] cmake -S \"{sourceDir}\" -B \"{buildDir}\"");
            var configure = await CommandRunner.RunAsync(
                build.CMakePath,
                $"-S \"{sourceDir}\" -B \"{buildDir}\"",
                workingDirectory: buildDir,
                onOutputLine: onOutputLine,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!configure.Success)
            {
                _log.LogError("CMake configure failed with code {Code}", configure.ExitCode);
                return new BuildOutcome(false, configure.ExitCode, null);
            }
        }

        var parallel = build.Parallelism > 0 ? $" --parallel {build.Parallelism}" : " --parallel";
        var args = $"--build \"{buildDir}\" --config {build.Configuration} --target authserver worldserver{parallel}";
        onOutputLine?.Invoke($"[build] cmake {args}");

        var result = await CommandRunner.RunAsync(
            build.CMakePath, args, workingDirectory: buildDir,
            onOutputLine: onOutputLine, cancellationToken: cancellationToken).ConfigureAwait(false);

        var outputDir = result.Success ? LocateBinaryOutput(buildDir, build.Configuration) : null;
        if (result.Success)
            _log.LogInformation("Build succeeded. Binaries at {Dir}", outputDir);
        else
            _log.LogError("Build failed with code {Code}", result.ExitCode);

        return new BuildOutcome(result.Success, result.ExitCode, outputDir);
    }

    /// <summary>
    /// Find the directory that received the freshly built worldserver.exe. On MSVC multi-config
    /// generators this is typically <c>&lt;buildDir&gt;\bin\&lt;Configuration&gt;</c>.
    /// </summary>
    public static string? LocateBinaryOutput(string buildDir, string configuration)
    {
        var candidates = new[]
        {
            Path.Combine(buildDir, "bin", configuration),
            Path.Combine(buildDir, "bin"),
            Path.Combine(buildDir, configuration),
            buildDir,
        };
        return candidates.FirstOrDefault(dir =>
            Directory.Exists(dir) &&
            File.Exists(Path.Combine(dir, ServerKind.World.ExecutableName())));
    }
}
