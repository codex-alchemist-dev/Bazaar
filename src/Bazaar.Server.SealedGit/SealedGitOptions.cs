namespace Bazaar.Server.SealedGit;

/// <summary>Settings of sealed git (doc 61 Q1).</summary>
public sealed class SealedGitOptions
{
    /// <summary>Where the short-lived plaintext copy is made. Default is the system temp folder; point it at tmpfs or the encrypted volume (doc 63).</summary>
    public string? TempRoot { get; init; }
}
