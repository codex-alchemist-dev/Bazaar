using System.Globalization;
using Bazaar.Server.Providers.ClaudeCode;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Keeps the Claude Code terms acceptance in the settings store (key <see cref="ClaudeCodeSettings.TermsAcknowledgedAtName"/>,
/// docs/design/91 section 14 step 7), so it survives a restart and every change is audited. The first acceptance stands: a later
/// call does not move the time.
/// </summary>
public sealed class SettingsClaudeCodeAcknowledgementStore(ISettingStore settings) : IClaudeCodeAcknowledgementStore
{
    private const string _actorId = "claude-code-setup";

    public async ValueTask<DateTimeOffset?> GetAcknowledgedAtAsync(CancellationToken cancellationToken)
    {
        var text = await settings.GetAsync(ClaudeCodeSettings.TermsAcknowledgedAt, cancellationToken: cancellationToken).ConfigureAwait(false);
        return text.Length > 0 && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
    }

    public async ValueTask RecordAsync(DateTimeOffset acknowledgedAt, CancellationToken cancellationToken)
    {
        if (await GetAcknowledgedAtAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        var result = await settings.SetAsync(
            ClaudeCodeSettings.TermsAcknowledgedAt,
            acknowledgedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            new SettingActor(_actorId, SettingRole.Operator),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Outcome != SettingWriteOutcome.Accepted)
        {
            throw new InvalidOperationException("The terms acknowledgement could not be stored: " + result.Reason);
        }
    }
}
