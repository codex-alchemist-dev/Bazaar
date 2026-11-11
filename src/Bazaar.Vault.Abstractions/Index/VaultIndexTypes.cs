namespace Bazaar.Server.Vault.Index;

public enum VaultIndexMode
{
    /// <summary>The index already holds the head commit.</summary>
    UpToDate,

    /// <summary>Every document was re-read from git and written.</summary>
    Rebuilt,

    /// <summary>Only the documents changed since the indexed commit, and those whose links may now resolve differently, were written.</summary>
    Incremental,
}

/// <param name="IndexedCommit">The commit the index holds after the run.</param>
/// <param name="Mode">How the run brought the index up to date.</param>
/// <param name="DocumentsWritten">Documents whose rows were written, changed and link-affected together.</param>
/// <param name="DocumentsRemoved">Documents deleted from the index.</param>
/// <param name="CommitsRecorded">New <c>vault_commit</c> rows.</param>
/// <param name="InvalidPaths">Markdown files whose front matter did not validate. They are not indexed.</param>
public sealed record VaultIndexResult(
    string IndexedCommit,
    VaultIndexMode Mode,
    int DocumentsWritten,
    int DocumentsRemoved,
    int CommitsRecorded,
    IReadOnlyList<string> InvalidPaths);
