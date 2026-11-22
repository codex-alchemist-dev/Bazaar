namespace Bazaar.Server.Health;

/// <summary>How long raw health reports are kept (doc 25 setting <c>metrics.raw_retention_days</c>). The derived samples are not pruned.</summary>
public interface IRawRetentionSource
{
    /// <summary>The effective number of days. Callers clamp it to <see cref="RawRetention.MinDays"/> to <see cref="RawRetention.MaxDays"/>.</summary>
    Task<int> GetDaysAsync(CancellationToken cancellationToken = default);
}

/// <summary>Limits and default of the raw retention, matching the setting's own validation (1 to 3650, default 90).</summary>
public static class RawRetention
{
    public const int DefaultDays = 90;
    public const int MinDays = 1;
    public const int MaxDays = 3650;
}

/// <summary>A fixed retention, used until a host supplies the setting.</summary>
public sealed class FixedRawRetentionSource(int days) : IRawRetentionSource
{
    public Task<int> GetDaysAsync(CancellationToken cancellationToken = default) => Task.FromResult(days);
}
