namespace AzerothCoreControl.Core.Services;

/// <summary>
/// Picks the lines that explain a failed build out of the compiler's output. A failing AzerothCore build
/// emits thousands of lines and the handful that matter are buried in the middle, not at the end — the tail
/// is usually just MSBuild's summary.
/// </summary>
public static class BuildDiagnostics
{
    /// <summary>How many diagnostics are worth showing; past this the first few tell the same story.</summary>
    public const int MaxErrors = 25;

    // Substrings that mark a real diagnostic. MSVC ("file.cpp(12,3): error C2065: ..."), the linker
    // ("LINK : fatal error LNK1104: ..."), MSBuild, CMake, and Ninja all announce themselves differently.
    private static readonly string[] ErrorMarkers =
    {
        ": error", ": fatal error", "cmake error", "ninja: error", "error mkdir", "clang: error",
    };

    // Summary/echo lines that contain a marker but carry no information ("0 Error(s)", our own banner).
    private static readonly string[] NotDiagnostics =
    {
        "0 error(s)", "error(s)", "errors:", "[build]", "[configure]", "[review]",
    };

    /// <summary>
    /// Returns the distinct diagnostic lines from <paramref name="lines"/>, in the order emitted, capped at
    /// <see cref="MaxErrors"/>. MSVC repeats each error in its per-project summary, hence the dedupe.
    /// </summary>
    public static IReadOnlyList<string> ExtractErrors(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var lower = line.ToLowerInvariant();
            if (!ErrorMarkers.Any(lower.Contains))
                continue;
            if (NotDiagnostics.Any(lower.Contains))
                continue;

            // MSVC appends the originating project in brackets; the same error from two projects is one error.
            var key = Normalize(line);
            if (!seen.Add(key))
                continue;

            found.Add(line);
            if (found.Count >= MaxErrors)
                break;
        }

        return found;
    }

    /// <summary>Strip MSVC's trailing " [C:\path\project.vcxproj]" so duplicates collapse.</summary>
    private static string Normalize(string line)
    {
        var bracket = line.LastIndexOf(" [", StringComparison.Ordinal);
        return bracket > 0 && line.EndsWith(']') ? line[..bracket] : line;
    }
}
