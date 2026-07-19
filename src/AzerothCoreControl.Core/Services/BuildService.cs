using AzerothCoreControl.Core.Models;
using AzerothCoreControl.Core.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public sealed record BuildOutcome(bool Success, int ExitCode, string? BinaryOutputDir);

/// <summary>
/// Automates recompiling AzerothCore via CMake. Assumes the build directory was already configured
/// once by the user (the normal Windows install flow); this runs an incremental
/// <c>cmake --build &lt;buildDir&gt; --config RelWithDebInfo --target authserver worldserver</c>.
/// If the build directory has no CMake cache, it runs a configure step first.
/// </summary>
public sealed class BuildService
{
    /// <summary>
    /// The only configuration this app builds. RelWithDebInfo keeps the optimised codegen a live server
    /// needs while still emitting the PDBs that make a crash dump readable.
    /// </summary>
    public const string Configuration = "RelWithDebInfo";

    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;

    public BuildService(Func<AppSettings> settings, ILogger<BuildService>? logger = null)
    {
        _settings = settings;
        _log = logger ?? NullLogger<BuildService>.Instance;
    }

    /// <summary>Run the build, streaming compiler output line-by-line to <paramref name="onOutputLine"/>.</summary>
    /// <param name="clean">
    /// Rebuild everything from scratch rather than incrementally, and re-run CMake first.
    /// </param>
    /// <remarks>
    /// The re-configure is the half that usually matters. AzerothCore collects module sources with a glob at
    /// CONFIGURE time, so a module that gained or lost .cpp files — after a re-clone, or switching to a fork —
    /// leaves the generated build files referencing the old list. The result compiles and links cleanly while
    /// silently omitting the new code, which is far more confusing than an error.
    /// <para>
    /// This only ever touches the BUILD directory. The run directory, and the user's .conf files in it, are
    /// the deploy step's business and are not affected by how the binaries were produced.
    /// </para>
    /// </remarks>
    public async Task<BuildOutcome> BuildAsync(
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default,
        bool clean = false)
    {
        var s = _settings();
        var build = s.Build;
        var buildDir = s.BuildDirectory;
        var sourceDir = s.SourceDirectory;

        if (string.IsNullOrWhiteSpace(buildDir))
            throw new InvalidOperationException("Build directory is not configured.");

        Directory.CreateDirectory(buildDir);

        // Optional review gate: hand the user cmake-gui and wait. This runs before the configure check
        // below, so generating from the GUI also satisfies it — no second headless configure.
        if (build.ReviewCMakeBeforeBuild)
        {
            var gui = ResolveCMakeGui(build);
            // Without a source dir cmake-gui can still open an already-generated tree from -B alone.
            var guiArgs = string.IsNullOrWhiteSpace(sourceDir)
                ? $"-B \"{buildDir}\""
                : $"-S \"{sourceDir}\" -B \"{buildDir}\"";
            onOutputLine?.Invoke($"[review] Opening {gui} — close it to continue the build.");
            try
            {
                await InteractiveProcessRunner.RunAsync(
                    gui,
                    guiArgs,
                    workingDirectory: buildDir,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // The user asked to review every build; quietly building unreviewed would defeat that, so
                // stop with something actionable instead.
                _log.LogError(ex, "Could not open cmake-gui");
                onOutputLine?.Invoke($"[review] {ex.Message}");
                onOutputLine?.Invoke("[review] Set the cmake-gui path in Settings, or turn off \"Review CMake settings before building\".");
                return new BuildOutcome(false, -1, null);
            }
            onOutputLine?.Invoke("[review] cmake-gui closed — building.");
        }

        // Configure if the build tree hasn't been generated yet — or unconditionally for a clean rebuild, so
        // the module source globs are re-evaluated.
        if (clean || !File.Exists(Path.Combine(buildDir, "CMakeCache.txt")))
        {
            if (string.IsNullOrWhiteSpace(sourceDir))
                throw new InvalidOperationException(clean
                    ? "Source directory is required to re-run CMake for a clean rebuild."
                    : "Source directory is required for the initial CMake configure.");

            // -DCMAKE_BUILD_TYPE is what single-config generators (Ninja, Makefiles) read; multi-config ones
            // (Visual Studio) ignore it and take --config at build time instead. Set both so either lands on
            // RelWithDebInfo.
            var configureArgs = $"-S \"{sourceDir}\" -B \"{buildDir}\" -DCMAKE_BUILD_TYPE={Configuration}";
            onOutputLine?.Invoke($"[configure] cmake {configureArgs}");
            var configure = await CommandRunner.RunAsync(
                build.CMakePath,
                configureArgs,
                workingDirectory: buildDir,
                onOutputLine: onOutputLine,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!configure.Success)
            {
                _log.LogError("CMake configure failed with code {Code}", configure.ExitCode);
                return new BuildOutcome(false, configure.ExitCode, null);
            }
        }

        var args = BuildArgs(buildDir!, build.Parallelism, clean);
        onOutputLine?.Invoke($"[build] cmake {args}");

        var result = await CommandRunner.RunAsync(
            build.CMakePath, args, workingDirectory: buildDir,
            onOutputLine: onOutputLine, cancellationToken: cancellationToken).ConfigureAwait(false);

        var outputDir = result.Success ? LocateBinaryOutput(buildDir, Configuration) : null;
        if (result.Success)
            _log.LogInformation("Build succeeded. Binaries at {Dir}", outputDir);
        else
            _log.LogError("Build failed with code {Code}", result.ExitCode);

        return new BuildOutcome(result.Success, result.ExitCode, outputDir);
    }

    /// <summary>The <c>cmake --build</c> argument line.</summary>
    /// <remarks>
    /// <c>--clean-first</c> deletes this target's artifacts and rebuilds them. It does NOT clear the CMake
    /// cache, which is why a clean build re-runs the configure step as well.
    /// </remarks>
    internal static string BuildArgs(string buildDir, int parallelism, bool clean)
    {
        var parallel = parallelism > 0 ? $" --parallel {parallelism}" : " --parallel";
        var cleanFirst = clean ? " --clean-first" : "";
        return $"--build \"{buildDir}\" --config {Configuration} --target authserver worldserver{parallel}{cleanFirst}";
    }

    /// <summary>
    /// Locate cmake-gui: an explicit setting wins, otherwise look beside cmake itself (they ship in the same
    /// bin folder), and fall back to bare "cmake-gui" for PATH lookup.
    /// </summary>
    public static string ResolveCMakeGui(BuildSettings build)
    {
        if (!string.IsNullOrWhiteSpace(build.CMakeGuiPath))
            return build.CMakeGuiPath!;

        var cmakeDir = Path.GetDirectoryName(build.CMakePath);
        if (!string.IsNullOrWhiteSpace(cmakeDir))
        {
            var sibling = Path.Combine(cmakeDir, OperatingSystem.IsWindows() ? "cmake-gui.exe" : "cmake-gui");
            if (File.Exists(sibling))
                return sibling;
        }
        return "cmake-gui";
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
