namespace Bazaar.Server.Git;

/// <summary>A repository on any provider. <paramref name="Provider"/> is the provider id, for example <c>github</c> or <c>gitlab</c>.</summary>
public sealed record RepoRef(string Provider, string FullName);

/// <summary>A branch name, without the <c>refs/heads/</c> prefix.</summary>
public sealed record BranchName(string Value);

/// <summary>A checked-out worktree on the host (doc 17). Tokens never enter a VM, so pushes run host-side (doc 21).</summary>
public sealed record WorktreeHandle(string Path, BranchName Branch);

/// <summary>The workspace a push reads from (doc 21, <c>IGitProvider.PushAsync</c>).</summary>
public sealed record WorkspaceRef(string Path);

/// <summary>A commit to make in a worktree. <paramref name="Paths"/> lists the files to stage.</summary>
public sealed record CommitRequest(string Message, IReadOnlyList<string> Paths);

/// <summary>The sha of a commit.</summary>
public sealed record CommitId(string Sha);

/// <summary>A pull or merge request on a provider. <paramref name="Number"/> is the provider's own number.</summary>
public sealed record PrRef(string Provider, string FullName, long Number);
