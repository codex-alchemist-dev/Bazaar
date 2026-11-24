using Bazaar.Community.Profile;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Community.Tools;

/// <summary>
/// One operator-configured agent identity on one platform. Bound from <see cref="ConfiguredCommunityAgentIdentitySource.SectionName"/>
/// as a list. The contact is a per-person field (doc 04); until encryption of personal fields is wired it sits in operator config.
/// </summary>
public sealed class CommunityProfileEntry
{
    public string AgentId { get; set; } = "";

    public string Platform { get; set; } = "";

    public string SponsorId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string SponsorName { get; set; } = "";

    public string Contact { get; set; } = "";

    public string DisclosureUrl { get; set; } = "";
}

/// <summary>
/// Default <see cref="ICommunityAgentIdentitySource"/> (doc 36, "Bot profile conventions", PROPOSED): the operator lists each agent's
/// sponsor and disclosed profile per platform in <c>Bazaar:Community:Profiles</c>. Fails closed: an agent or platform that is not listed,
/// an entry with a bad agent id, or a profile that fails <see cref="BotProfileBuilder.Validate"/> yields null, so the propose tool refuses.
/// </summary>
public sealed class ConfiguredCommunityAgentIdentitySource : ICommunityAgentIdentitySource
{
    /// <summary>Configuration section holding the list of <see cref="CommunityProfileEntry"/>.</summary>
    public const string SectionName = "Bazaar:Community:Profiles";

    private readonly IReadOnlyList<CommunityProfileEntry> _entries;

    public ConfiguredCommunityAgentIdentitySource(IEnumerable<CommunityProfileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = [.. entries];
    }

    /// <summary>Reads the entries from <paramref name="configuration"/>; a missing section means no profiles.</summary>
    public static ConfiguredCommunityAgentIdentitySource From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ConfiguredCommunityAgentIdentitySource(configuration.GetSection(SectionName).Get<List<CommunityProfileEntry>>() ?? []);
    }

    /// <inheritdoc />
    public Task<CommunityAgentIdentity?> FindAsync(Guid agentId, string platform, CancellationToken ct = default)
    {
        foreach (var e in _entries)
        {
            if (!Guid.TryParse(e.AgentId, out var id) || id != agentId
                || !string.Equals(e.Platform?.Trim(), platform?.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(e.SponsorId)
                || !Uri.TryCreate(e.DisclosureUrl, UriKind.Absolute, out var url))
            {
                continue;
            }

            var profile = new BotProfile(id.ToString("D"), e.Platform!.Trim().ToLowerInvariant(), e.DisplayName, e.SponsorName, e.Contact, url);
            if (BotProfileBuilder.Validate(profile).Count > 0) continue;
            return Task.FromResult<CommunityAgentIdentity?>(new CommunityAgentIdentity(e.SponsorId.Trim(), profile));
        }

        return Task.FromResult<CommunityAgentIdentity?>(null);
    }
}
