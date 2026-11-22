using Bazaar.Server.CodingGit;

namespace Bazaar.Server.Host.Coding;

/// <summary>Operator settings for the commit author (section <c>Coding:Harness:Git</c>).</summary>
public sealed class AgentGitIdentityOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Coding:Harness:Git";

    /// <summary>Configuration key of <see cref="AppNoreplyEmail"/>, used in startup errors.</summary>
    public const string AppNoreplyEmailKey = SectionName + ":AppNoreplyEmail";

    /// <summary>The GitHub App's noreply address, written as the commit author email. Required; no domain is built in.</summary>
    public string? AppNoreplyEmail { get; set; }

    /// <summary>Checks the options and throws naming the key. Fails closed: a bad value stops the host start.</summary>
    /// <exception cref="InvalidOperationException">The email is missing or malformed.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppNoreplyEmail))
        {
            throw new InvalidOperationException($"{AppNoreplyEmailKey} is required when Coding:Harness:Enabled is true: set the GitHub App's noreply address.");
        }

        if (!GitIdentityText.IsSafeEmail(AppNoreplyEmail))
        {
            throw new InvalidOperationException($"{AppNoreplyEmailKey} is not a plain address (one '@', no spaces, angle brackets or control characters).");
        }
    }
}

/// <summary>The agent's human-readable name, used as the commit author name.</summary>
public interface IAgentDisplayNames
{
    /// <summary>The display name, or null when the agent is unknown.</summary>
    ValueTask<string?> FindAsync(string agentId, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IAgentGitIdentity"/> (doc 17, "Host-side sources"): author name is the agent's display name and author
/// email is the operator-set GitHub App noreply address. An unknown agent or an unusable name throws, so the commit fails;
/// nothing is defaulted.
/// </summary>
public sealed class ConfiguredAgentGitIdentity : IAgentGitIdentity
{
    private readonly IAgentDisplayNames _names;
    private readonly string _email;

    /// <summary>Creates the identity source. Validates the options.</summary>
    public ConfiguredAgentGitIdentity(IAgentDisplayNames names, AgentGitIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _names = names;
        _email = options.AppNoreplyEmail!.Trim();
    }

    /// <inheritdoc />
    public async ValueTask<GitAuthor> GetAuthorAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var name = await _names.FindAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (name is null)
        {
            throw new InvalidOperationException($"No display name is known for agent '{agentId}', so no commit author can be built.");
        }

        name = name.Trim();
        if (!GitIdentityText.IsSafeName(name))
        {
            throw new InvalidOperationException($"The display name of agent '{agentId}' is empty or has control characters or angle brackets.");
        }

        return new GitAuthor(name, _email);
    }
}

/// <summary>Display names read from configuration (<c>Coding:Harness:Git:AgentNames:&lt;agentId&gt;</c>), until a roster source exists.</summary>
public sealed class ConfiguredAgentDisplayNames : IAgentDisplayNames
{
    private readonly Dictionary<string, string> _names;

    /// <summary>Creates the source over a copy of the map.</summary>
    public ConfiguredAgentDisplayNames(IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public ValueTask<string?> FindAsync(string agentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_names.TryGetValue(agentId, out var name) ? name : null);
}

internal static class GitIdentityText
{
    public static bool IsSafeName(string name) =>
        name.Length > 0 && !name.Any(c => char.IsControl(c) || c is '<' or '>');

    public static bool IsSafeEmail(string email)
    {
        var value = email.Trim();
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0
            && at == value.LastIndexOf('@')
            && at < value.Length - 1
            && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>');
    }
}
