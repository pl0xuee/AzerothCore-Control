using AzerothCoreControl.Core.Models;

namespace AzerothCoreControl.Core.Services;

/// <summary>Result of a first-run auto-detection scan.</summary>
public sealed class DetectedPaths
{
    public string? RunDirectory { get; init; }
    public string? SourceDirectory { get; init; }
    public string? BuildDirectory { get; init; }
    public bool HasWorldServer { get; init; }
    public bool HasAuthServer { get; init; }
    public bool HasModulesFolder { get; init; }
}

/// <summary>
/// Best-effort discovery of an AzerothCore install so the setup wizard can pre-fill paths.
/// Everything is heuristic and the user confirms before anything is saved.
/// </summary>
public static class PathDetector
{
    private static readonly string[] CommonRoots =
    {
        @"C:\azerothcore",
        @"C:\AzerothCore",
        @"C:\Games\azerothcore",
        @"C:\Servers\azerothcore",
    };

    /// <summary>Scan a starting directory (and a few common roots) for the pieces of an install.</summary>
    public static DetectedPaths Detect(string? startFrom = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(startFrom))
            candidates.Add(startFrom);
        candidates.AddRange(CommonRoots);

        string? runDir = null, sourceDir = null, buildDir = null;

        foreach (var root in candidates.Where(Directory.Exists).Distinct())
        {
            runDir ??= FindDirContaining(root, ServerKind.World.ExecutableName());
            sourceDir ??= FindModulesParent(root);
            buildDir ??= FindDirNamed(root, "build");
            if (runDir != null && sourceDir != null)
                break;
        }

        bool hasWorld = runDir != null && File.Exists(Path.Combine(runDir, ServerKind.World.ExecutableName()));
        bool hasAuth = runDir != null && File.Exists(Path.Combine(runDir, ServerKind.Auth.ExecutableName()));
        bool hasModules = sourceDir != null && Directory.Exists(Path.Combine(sourceDir, "modules"));

        return new DetectedPaths
        {
            RunDirectory = runDir,
            SourceDirectory = sourceDir,
            BuildDirectory = buildDir,
            HasWorldServer = hasWorld,
            HasAuthServer = hasAuth,
            HasModulesFolder = hasModules,
        };
    }

    /// <summary>Apply detected paths onto settings without clobbering values the user already set.</summary>
    public static void ApplyTo(AppSettings settings, DetectedPaths detected)
    {
        settings.RunDirectory ??= detected.RunDirectory;
        settings.SourceDirectory ??= detected.SourceDirectory;
        settings.BuildDirectory ??= detected.BuildDirectory;
        settings.DeployDirectory ??= detected.RunDirectory;
    }

    private static string? FindDirContaining(string root, string fileName, int maxDepth = 4)
        => EnumerateDirs(root, maxDepth).FirstOrDefault(d => File.Exists(Path.Combine(d, fileName)));

    private static string? FindDirNamed(string root, string name, int maxDepth = 3)
        => EnumerateDirs(root, maxDepth)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));

    private static string? FindModulesParent(string root, int maxDepth = 3)
        => EnumerateDirs(root, maxDepth)
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "modules")));

    /// <summary>Breadth-limited directory walk that swallows access errors.</summary>
    private static IEnumerable<string> EnumerateDirs(string root, int maxDepth)
    {
        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            yield return dir;
            if (depth >= maxDepth)
                continue;

            string[] children;
            try
            {
                children = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var child in children)
                queue.Enqueue((child, depth + 1));
        }
    }
}
