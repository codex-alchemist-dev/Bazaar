using System.Text;

namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Offsite Git target (design doc 48, section 5.3). For each digest it writes a small file named by the digest hex under a
/// configured local repository, commits it and pushes the commit to a configured remote branch. The receipt is the commit id.
/// </summary>
/// <remarks>
/// The local path should be a repository kept for anchors only. A commit is made even when nothing changed, so a retry after
/// a failed push still produces a commit to push. A non-fast-forward push is refused by the remote, which keeps the anchor
/// history append-only.
/// </remarks>
public sealed class OffsiteGitTarget : IAuditAnchorTarget
{
    /// <summary>Stable target identifier. Named in receipts and in audit events.</summary>
    public const string Id = "offsite-git";

    private const int _digestSize = 32;

    private readonly IGitRunner _git;
    private readonly OffsiteGitTargetOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the target. Throws <see cref="ArgumentException"/> when the options are incomplete.</summary>
    public OffsiteGitTarget(IGitRunner git, OffsiteGitTargetOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        options.Validate();

        _git = git;
        _options = options;
        _time = time;
    }

    /// <inheritdoc />
    public string TargetId => Id;

    /// <inheritdoc />
    public async Task<AuditAnchorReceipt> AnchorAsync(ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct)
    {
        ValidateDigest(checkpointDigest.Span);
        var hex = Convert.ToHexString(checkpointDigest.Span).ToLowerInvariant();
        var fileName = FileNameFor(hex);

        // The file holds the digest and nothing else, so the remote learns no checkpoint content.
        await File.WriteAllTextAsync(Path.Combine(_options.LocalPath, fileName), hex + "\n", ct).ConfigureAwait(false);

        await RunGitAsync("add", ["add", "--", fileName], ct).ConfigureAwait(false);
        await RunGitAsync(
            "commit",
            [
                "-c", $"user.name={_options.AuthorName}",
                "-c", $"user.email={_options.AuthorEmail}",
                "commit", "--allow-empty", "--no-verify", "-m", $"audit anchor {hex}",
            ],
            ct).ConfigureAwait(false);

        var head = await RunGitAsync("rev-parse", ["rev-parse", "--verify", "HEAD"], ct).ConfigureAwait(false);
        var commit = head.StandardOutput.Trim();
        await RunGitAsync("push", ["push", _options.RemoteName, $"{commit}:refs/heads/{_options.Branch}"], ct).ConfigureAwait(false);

        return new AuditAnchorReceipt(TargetId, Encoding.ASCII.GetBytes(commit), TruncateToMicroseconds(_time.GetUtcNow()));
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(AuditAnchorReceipt receipt, ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateDigest(checkpointDigest.Span);

        if (receipt.TargetId != TargetId)
        {
            return false;
        }

        var commit = Encoding.ASCII.GetString(receipt.ReceiptBytes);
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
        {
            return false;
        }

        // The commit must contain the file for this digest, with the digest as its content.
        var hex = Convert.ToHexString(checkpointDigest.Span).ToLowerInvariant();
        var shown = await _git.RunAsync(_options.LocalPath, ["show", $"{commit}:{FileNameFor(hex)}"], ct).ConfigureAwait(false);
        return shown.ExitCode == 0 && shown.StandardOutput.Trim() == hex;
    }

    private static string FileNameFor(string hex) => $"anchor-{hex}.txt";

    private async Task<GitResult> RunGitAsync(string subcommand, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await _git.RunAsync(_options.LocalPath, arguments, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // The exit code and the subcommand only. Git's own text can echo remote URLs, so it is not put in the message.
            throw new InvalidOperationException($"git {subcommand} failed with exit code {result.ExitCode}.");
        }

        return result;
    }

    private static void ValidateDigest(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != _digestSize)
        {
            throw new ArgumentException($"Checkpoint digest must be {_digestSize} bytes.", nameof(digest));
        }
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}

/// <summary>
/// Settings for the offsite Git target. The local path, remote name and branch come from the operator. Credentials are never
/// part of these settings: the remote authenticates with its own configuration or the environment.
/// </summary>
public sealed record OffsiteGitTargetOptions
{
    /// <summary>Whether this target is used when anchoring is enabled. The shipped default is on, and it still needs a path.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Path to the local working copy that holds the anchor repository. Empty until the operator sets it.</summary>
    public string LocalPath { get; init; } = "";

    /// <summary>Name of the configured remote to push to.</summary>
    public string RemoteName { get; init; } = "origin";

    /// <summary>Branch on the remote that anchor commits are pushed to.</summary>
    public string Branch { get; init; } = "audit-anchors";

    /// <summary>Author name on anchor commits.</summary>
    public string AuthorName { get; init; } = "Bazaar audit anchor";

    /// <summary>Author address on anchor commits. A placeholder; the operator sets a real address.</summary>
    public string AuthorEmail { get; init; } = "audit-anchor@example.invalid";

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when a value is missing or could be read as a git option. The remote and branch names
    /// must not start with a dash and must not contain whitespace or control characters. The author fields may hold spaces, but not
    /// control characters, and must not start with a dash.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            throw new ArgumentException("The offsite Git target needs a local repository path.", nameof(LocalPath));
        }

        RequireRefName(RemoteName, nameof(RemoteName));
        RequireRefName(Branch, nameof(Branch));
        RequireText(AuthorName, nameof(AuthorName));
        RequireText(AuthorEmail, nameof(AuthorEmail));
    }

    private static void RequireRefName(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("Must be a non-empty name without whitespace or control characters, not starting with a dash.", paramName);
        }
    }

    private static void RequireText(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(char.IsControl))
        {
            throw new ArgumentException("Must be non-empty text without control characters, not starting with a dash.", paramName);
        }
    }
}
