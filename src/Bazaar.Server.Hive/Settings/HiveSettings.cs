using System.Text.Json.Serialization;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Hive.Settings;

/// <summary>One entry of <c>hive.grants</c>: lessons of <see cref="Kind"/> from <see cref="FromProject"/> may be copied for <see cref="ToProject"/>.</summary>
/// <param name="FromProject">Source project slug.</param>
/// <param name="ToProject">Target project slug.</param>
/// <param name="Kind">Wire name of the kind: <c>procedure</c>, <c>failure</c> or <c>technical-fact</c>.</param>
public sealed record HiveGrantEntry(
    [property: JsonPropertyName("from_project")] string FromProject,
    [property: JsonPropertyName("to_project")] string ToProject,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>
/// The Hive keys of docs/design/08-memory-system.md (settings table): <c>hive.grants</c> and <c>hive.allowed_kinds</c>. Both are
/// install scope and operator-changed; every change is audited by the settings store. Defaults are the protective ones: no grants
/// (nothing transfers) and procedure and failure only (technical facts are an explicit opt-in).
/// </summary>
/// <remarks>
/// The table also says a change needs approval (Council vote). The settings framework has no Council tier (<see cref="ApprovalTier"/>
/// is None or Tier2), so a key that widens beyond the default (a technical-fact opt-in) is held for owner approval as Tier2, and
/// narrowing or removing never waits. Grants themselves apply at once, audited, because revoking one must never be held.
/// Kinds are the wire names of <see cref="HiveKinds"/>, so an unknown class (preference, reputation, private memory) cannot be added.
/// </remarks>
public static class HiveSettings
{
    /// <summary>The setting name of <see cref="Grants"/>.</summary>
    public const string GrantsName = "hive.grants";

    /// <summary>The setting name of <see cref="AllowedKinds"/>.</summary>
    public const string AllowedKindsName = "hive.allowed_kinds";

    /// <summary>The owner's grant matrix. Empty by default: nothing transfers.</summary>
    public static readonly SettingKey<IReadOnlyList<HiveGrantEntry>> Grants = new(
        GrantsName, [], SettingScope.Install, SettingRole.Operator,
        "Project pairs and kinds the Hive Mind may copy lessons between (source project, target project, kind). Empty means nothing transfers.",
        validate: CheckGrants);

    /// <summary>The kinds a Hive copy may carry. Technical facts are an explicit opt-in.</summary>
    public static readonly SettingKey<IReadOnlyList<string>> AllowedKinds = new(
        AllowedKindsName, [HiveKinds.Name(HiveKind.Procedure), HiveKinds.Name(HiveKind.Failure)], SettingScope.Install, SettingRole.Operator,
        "Kinds of lesson a Hive copy may carry: procedure, failure and, as an explicit opt-in, technical-fact.",
        ApprovalTier.Tier2,
        validate: CheckKinds,
        requiresApprovalFor: kinds => kinds.Contains(HiveKinds.Name(HiveKind.TechnicalFact), StringComparer.Ordinal));

    /// <summary>Every Hive key.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = [Grants, AllowedKinds];

    /// <summary>Parses a stored grant entry. Throws <see cref="InvalidOperationException"/> for an unknown kind.</summary>
    public static HiveGrant ToGrant(HiveGrantEntry entry, string grantedBy)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return HiveKinds.TryParse(entry.Kind, out var kind)
            ? new HiveGrant(entry.FromProject, entry.ToProject, kind, grantedBy)
            : throw new InvalidOperationException($"{GrantsName} holds an unknown kind '{entry.Kind}'.");
    }

    private static string? CheckGrants(IReadOnlyList<HiveGrantEntry> grants)
    {
        var seen = new HashSet<(string, string, string)>();
        foreach (var grant in grants)
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.FromProject) || string.IsNullOrWhiteSpace(grant.ToProject))
            {
                return $"'{GrantsName}' entries need a source and a target project.";
            }

            if (string.Equals(grant.FromProject, grant.ToProject, StringComparison.Ordinal))
            {
                return $"'{GrantsName}' needs two different projects; got '{grant.FromProject}' twice.";
            }

            if (!HiveKinds.TryParse(grant.Kind, out _))
            {
                return $"'{GrantsName}' kind '{grant.Kind}' must be procedure, failure or technical-fact.";
            }

            if (!seen.Add((grant.FromProject, grant.ToProject, grant.Kind)))
            {
                return $"'{GrantsName}' lists {grant.FromProject} to {grant.ToProject} ({grant.Kind}) twice.";
            }
        }

        return null;
    }

    private static string? CheckKinds(IReadOnlyList<string> kinds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            if (!HiveKinds.TryParse(kind, out _))
            {
                return $"'{AllowedKindsName}' holds '{kind}'. Use procedure, failure or technical-fact.";
            }

            if (!seen.Add(kind))
            {
                return $"'{AllowedKindsName}' lists '{kind}' twice.";
            }
        }

        return null;
    }
}

/// <summary>The Hive module's contribution to the settings registry. <see cref="HiveComponent"/> registers it.</summary>
public sealed class HiveSettingContribution : ISettingContribution
{
    /// <inheritdoc />
    public IReadOnlyList<SettingDefinition> Settings => HiveSettings.All;
}
