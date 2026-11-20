namespace Bazaar.Server.SealedGit;

/// <summary>Settings of sealed git (doc 61 Q1).</summary>
public sealed class SealedGitOptions
{
    /// <summary>Where the short-lived plaintext copy is made, owner-only. Point it at tmpfs or the encrypted volume (doc 63). When null the system temp folder is used and a <c>sealgit.temp_root_default</c> warning is audited.</summary>
    public string? TempRoot { get; init; }

    /// <summary>Absolute path of the git executable for <see cref="GitCommand"/> callers; null resolves <c>git</c> on PATH.</summary>
    public string? GitPath { get; init; }

    /// <summary>How long a git operation waits for the scope lease (the cross-process lock over a whole materialise, work, reseal cycle) before it throws <see cref="TimeoutException"/>. Default 2 minutes.</summary>
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
