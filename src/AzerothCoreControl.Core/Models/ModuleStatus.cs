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

    /// <summary>GitHub "owner/repo" — from the origin remote, or from the catalogue when there's no git repo.</summary>
    public string? GitHubRepo { get; init; }

    /// <summary>False when the folder has no git checkout (e.g. installed by unzipping a download).</summary>
    public bool IsGitRepo { get; init; } = true;

    /// <summary>
    /// Set when <see cref="GitHubRepo"/> came from the AzerothCore catalogue (matched on folder name) rather
    /// than from a git remote — i.e. it's an educated identification, not a fact recorded by the install.
    /// </summary>
    public bool IdentifiedFromCatalogue { get; init; }

    /// <summary>Clone URL for re-installing this module properly with git. Null if unknown.</summary>
    public string? CloneUrl { get; init; }

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

    /// <summary>
    /// The repo this module is pinned to in settings, set ONLY when that pin disagrees with the origin remote
    /// the checkout actually uses ("owner/repo"). Null when there is no pin, or the pin already matches.
    /// </summary>
    /// <remarks>
    /// A pin is the user saying "this module is really that repo" — usually because they run a fork. For a
    /// folder with no git metadata that is the only identification available, but for a real checkout the
    /// remote is the fact and the pin is an intention, and the two can disagree. Naming the disagreement lets
    /// the app offer to act on it instead of silently checking updates against a repo the user has told us is
    /// the wrong one.
    /// </remarks>
    public string? PinnedRepo { get; init; }

    /// <summary>Clone URL of <see cref="PinnedRepo"/>, for repointing the remote at it.</summary>
    public string? PinnedCloneUrl { get; init; }

    /// <summary>The checkout's origin disagrees with the repo it's pinned to in settings.</summary>
    public bool HasRemoteMismatch => PinnedRepo != null && PinnedCloneUrl != null;

    public bool UpdateAvailable => BehindBy > 0;

    /// <summary>A pull can fast-forward cleanly only if we're behind, not ahead, and the tree is clean.</summary>
    public bool CanFastForward => BehindBy > 0 && AheadBy == 0 && !HasLocalChanges;
}
