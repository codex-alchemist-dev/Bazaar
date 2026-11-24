using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Projects;

/// <summary>Who may add members (<c>projects.member_adders</c>, doc 93 section 6). <c>council</c> waits on the Council rule of doc 10 and is refused at start.</summary>
public enum MemberAdders
{
    /// <summary>The owner and a current lead of the project (the default).</summary>
    LeadAndOwner,

    /// <summary>Only the owner. A tightening of the default.</summary>
    OwnerOnly,
}

/// <summary>
/// The <c>project.*</c> settings of doc 93 section 6, bound from <c>Bazaar:Projects</c>. Settings may tighten, never loosen below the
/// owner-only floor: creators is always the owner, and a value that does not parse fails at start instead of loosening a limit.
/// </summary>
/// <param name="LoansGrantRead"><c>projects.loans_grant_read</c>. False limits membership to <c>project_member</c> rows.</param>
/// <param name="MemberAdders"><c>projects.member_adders</c>.</param>
public sealed record ProjectsOptions(bool LoansGrantRead = true, MemberAdders MemberAdders = MemberAdders.LeadAndOwner)
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Bazaar:Projects";

    /// <summary>Reads the options. Absent keys keep the defaults.</summary>
    public static ProjectsOptions Read(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var options = new ProjectsOptions();

        if (section["LoansGrantRead"] is { Length: > 0 } loans)
        {
            if (!bool.TryParse(loans, out var parsed))
            {
                throw new InvalidOperationException($"{SectionName}:LoansGrantRead must be true or false.");
            }

            options = options with { LoansGrantRead = parsed };
        }

        if (section["Creators"] is { Length: > 0 } creators && !string.Equals(creators, "owner", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{SectionName}:Creators supports only 'owner' (the floor).");
        }

        if (section["MemberAdders"] is { Length: > 0 } adders)
        {
            options = options with
            {
                MemberAdders = adders switch
                {
                    "lead_and_owner" => MemberAdders.LeadAndOwner,
                    "owner" => MemberAdders.OwnerOnly,
                    _ => throw new InvalidOperationException(
                        $"{SectionName}:MemberAdders '{adders}' is not supported. Use lead_and_owner or owner."),
                },
            };
        }

        return options;
    }
}
