using System.Globalization;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>
/// The Claude Code settings key (docs/design/91 section 14, step 7). PROPOSED: the doc 51 settings table does not list it yet.
/// It holds the time the operator accepted the terms summary (rules T6 and T10), as an ISO 8601 UTC string, or empty when not
/// accepted. Install scope, operator-changed, audited by the settings store like every key.
/// </summary>
public static class ClaudeCodeSettings
{
    public const string TermsAcknowledgedAtName = "gateway.claude_code.terms_acknowledged_at";

    public static readonly SettingKey<string> TermsAcknowledgedAt = new(
        TermsAcknowledgedAtName, "", SettingScope.Install, SettingRole.Operator,
        "When the operator accepted the Claude Code terms summary (ISO 8601 UTC). Empty means not accepted.",
        validate: value => value.Length == 0 || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            ? null
            : $"'{TermsAcknowledgedAtName}' must be empty or an ISO 8601 time.");

    public static IReadOnlyList<SettingDefinition> All { get; } = [TermsAcknowledgedAt];
}

/// <summary>The module's contribution to the settings registry.</summary>
public sealed class ClaudeCodeSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => ClaudeCodeSettings.All;
}
