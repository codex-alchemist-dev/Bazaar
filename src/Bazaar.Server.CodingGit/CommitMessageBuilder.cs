using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.CodingGit;

/// <summary>The model credited on a commit as <c>Co-Authored-By</c>.</summary>
/// <param name="Name">Display name, for example <c>Claude Sonnet 5.5</c>.</param>
/// <param name="Email">Credit email.</param>
public sealed record ModelIdentity(string Name, string Email);

/// <summary>A validated commit message and its trailers.</summary>
/// <param name="Message">Subject and body, scrubbed, without trailers.</param>
/// <param name="Trailers">The trailers the engine appends.</param>
public sealed record ValidatedCommit(string Message, CommitTrailers Trailers)
{
    /// <summary>Renders subject, body and trailers as one commit message, for engines that take a full message.</summary>
    public string Render()
    {
        return $"{Message}\n\nCo-Authored-By: {Trailers.CoAuthoredBy}\nBazaar-Agent: {Trailers.AgentId}\nBazaar-GC: {Trailers.GcId}\nBazaar-Task: {Trailers.TaskId}\n";
    }
}

/// <summary>
/// Builds commit messages with the doc 17 trailers: <c>Co-Authored-By</c> for the model plus agent, GC and task ids
/// (the trailer names match the host workspace). It throws on any <c>Signed-off-by</c> in any input, strips lines that
/// spoof a reserved trailer, drops control characters, and refuses text that looks like a secret.
/// </summary>
public static partial class CommitMessageBuilder
{
    private const int _maxSubject = 200;
    private const int _maxBody = 20_000;

    /// <summary>Validates the inputs and returns the commit message with its trailers.</summary>
    /// <exception cref="CodingGitException">An input is malformed, carries a sign-off, or looks like a secret.</exception>
    public static ValidatedCommit Build(string subject, string? body, string agentId, string gcId, string taskId, ModelIdentity model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var cleanSubject = CleanSubject(subject);
        var cleanBody = CleanBody(body);

        var message = cleanBody.Length == 0 ? cleanSubject : $"{cleanSubject}\n\n{cleanBody}";
        SecretGuard.ThrowIfSecret(message);

        var credit = CreditLine(model);
        RejectSignOff(credit);
        var trailers = new CommitTrailers(IdValue(agentId), IdValue(gcId), IdValue(taskId), credit);
        return new ValidatedCommit(message, trailers);
    }

    private static string CleanSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw Invalid("The commit subject is empty.");
        }

        RejectSignOff(subject);
        var trimmed = subject.Trim();
        if (trimmed.Length > _maxSubject || trimmed.Any(char.IsControl))
        {
            throw Invalid("The commit subject must be one line of at most 200 characters.");
        }

        return trimmed;
    }

    private static string CleanBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        RejectSignOff(body);
        if (body.Length > _maxBody)
        {
            throw Invalid("The commit body is too long.");
        }

        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var kept = new StringBuilder();
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = new string([.. rawLine.Where(c => c == '\t' || !char.IsControl(c))]);
            if (ReservedTrailer().IsMatch(line))
            {
                continue;
            }

            kept.Append(line).Append('\n');
        }

        return kept.ToString().Trim();
    }

    private static string CreditLine(ModelIdentity model)
    {
        var name = model.Name?.Trim() ?? string.Empty;
        var email = model.Email?.Trim() ?? string.Empty;
        var bad = name.Length is 0 or > 100 || name.Any(c => char.IsControl(c) || c is '<' or '>')
            || email.Length is 0 or > 200 || email.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>')
            || !email.Contains('@', StringComparison.Ordinal);
        if (bad)
        {
            throw Invalid("The model identity is not a valid name and email.");
        }

        return $"{name} <{email}>";
    }

    private static string IdValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !IdPattern().IsMatch(value))
        {
            throw Invalid("Agent, GC and task ids must be 1 to 128 characters of [A-Za-z0-9._:-].");
        }

        return value;
    }

    private static void RejectSignOff(string text)
    {
        if (SignOff().IsMatch(text))
        {
            throw new CodingGitException(CodingGitError.ForbiddenTrailer, "Agents never add Signed-off-by; the platform signs off at merge.");
        }
    }

    private static CodingGitException Invalid(string message) => new(CodingGitError.InvalidRequest, message);

    // Any spelling of the sign-off key, anywhere in the text: fail closed.
    [GeneratedRegex(@"signed[\s_-]*off[\s_-]*by", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignOff();

    [GeneratedRegex(@"^\s*(co-authored-by|bazaar-agent|bazaar-gc|bazaar-task)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedTrailer();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
