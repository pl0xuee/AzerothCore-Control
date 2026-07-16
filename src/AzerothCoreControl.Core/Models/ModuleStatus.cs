namespace AzerothCoreControl.Core.Models;

/// <summary>A single commit that's on the remote but not yet pulled locally.</summary>
public sealed record ModuleCommit(string ShortSha, string Summary, string Author, DateTimeOffset Date);

/// <summary>Update status of a single installed AzerothCore module (a git repo under <c>modules/</c>).</summary>
public sealed class ModuleStatus
{
    public required string Name { get; init; }

    /// <summary>Absolute path to the module's working directory.</summary>
    public required string Path { get; init; }

    /// <summary>Currently checked-out branch, or null if detached HEAD.</summary>
    public string? Branch { get; init; }

    /// <summary>Short SHA of the local HEAD.</summary>
    public string? LocalCommit { get; init; }

    /// <summary>Short SHA of the remote tip for the tracked branch.</summary>
    public string? RemoteCommit { get; init; }

    /// <summary>GitHub "owner/repo" resolved from the origin remote, if it is a GitHub repo.</summary>
    public string? GitHubRepo { get; init; }

    /// <summary>How many commits the local branch is behind the remote. 0 = up to date.</summary>
    public int BehindBy { get; init; }

    /// <summary>How many local commits are not on the remote (dirty/ahead — pull may not fast-forward).</summary>
    public int AheadBy { get; init; }

    /// <summary>Working tree has uncommitted changes (a pull could conflict).</summary>
    public bool HasLocalChanges { get; init; }

    /// <summary>Tag name of the latest GitHub release, if the repo publishes releases.</summary>
    public string? LatestReleaseTag { get; init; }

    /// <summary>The commits on the remote that aren't pulled yet (newest first), for showing "what changed".</summary>
    public IReadOnlyList<ModuleCommit> IncomingCommits { get; init; } = Array.Empty<ModuleCommit>();

    /// <summary>Set when the status could not be computed (network/API/git error).</summary>
    public string? Error { get; init; }

    public bool UpdateAvailable => BehindBy > 0;

    /// <summary>A pull can fast-forward cleanly only if we're behind, not ahead, and the tree is clean.</summary>
    public bool CanFastForward => BehindBy > 0 && AheadBy == 0 && !HasLocalChanges;
}
