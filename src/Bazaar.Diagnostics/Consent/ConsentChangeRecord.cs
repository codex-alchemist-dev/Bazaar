namespace Bazaar.Diagnostics.Consent;

/// <summary>
/// One audited change to a crash-report setting (doc 43 section 11.5). Stored in the hash-chained audit log (doc 15),
/// which is not part of this type. Values are the registry's string form, so the record does not depend on the enums.
/// </summary>
/// <param name="Who">The actor who made the change (a person id or the operator's id, as the audit log records it).</param>
/// <param name="When">When the change was made, in UTC.</param>
/// <param name="Key">The registry key, one of <see cref="ConsentSettingKeys.All"/>.</param>
/// <param name="OldValue">The value before the change.</param>
/// <param name="NewValue">The value after the change. Never equal to <paramref name="OldValue"/>.</param>
/// <param name="Reason">Why the change was made. Required.</param>
public sealed record ConsentChangeRecord(string Who, DateTimeOffset When, string Key, string OldValue, string NewValue, string Reason);

/// <summary>The outcome of building a <see cref="ConsentChangeRecord"/>: a record, or the problem that refused it.</summary>
/// <param name="Record">The built record, or null when refused.</param>
/// <param name="Problem">The refusal message, or null when built.</param>
public sealed record ConsentChangeResult(ConsentChangeRecord? Record, string? Problem)
{
    /// <summary>True when a record was built.</summary>
    public bool Succeeded => Record is not null;
}

/// <summary>
/// Builds audit records for consent changes. Pure: the caller supplies the time. Refuses a no-op change, an unknown
/// key, and a change without a who or a reason, and returns the problem rather than throwing.
/// </summary>
public static class ConsentChangeRecordBuilder
{
    /// <summary>Builds the record, or refuses it with a message.</summary>
    public static ConsentChangeResult Build(string who, DateTimeOffset when, string key, string oldValue, string newValue, string reason)
    {
        ArgumentNullException.ThrowIfNull(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);

        var trimmedWho = who?.Trim() ?? string.Empty;
        var trimmedKey = key?.Trim() ?? string.Empty;
        var trimmedReason = reason?.Trim() ?? string.Empty;

        if (trimmedWho.Length == 0)
        {
            return Refuse("A consent change needs the actor who made it.");
        }

        if (!ConsentSettingKeys.All.Contains(trimmedKey, StringComparer.Ordinal))
        {
            return Refuse($"'{trimmedKey}' is not a crash-report setting key.");
        }

        if (trimmedReason.Length == 0)
        {
            return Refuse($"A change to {trimmedKey} needs a reason.");
        }

        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return Refuse($"{trimmedKey} is already '{newValue}'; a no-op change is not recorded.");
        }

        var record = new ConsentChangeRecord(trimmedWho, when.ToUniversalTime(), trimmedKey, oldValue, newValue, trimmedReason);
        return new ConsentChangeResult(record, null);
    }

    private static ConsentChangeResult Refuse(string problem) => new(null, problem);
}
