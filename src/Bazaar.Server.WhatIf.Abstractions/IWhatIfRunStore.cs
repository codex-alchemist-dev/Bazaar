namespace Bazaar.Server.WhatIf;

/// <summary>Durable record of What If runs (<c>what_if_run</c>, one row per branch).</summary>
public interface IWhatIfRunStore
{
    /// <summary>Inserts the run, or replaces every branch row of the same run id.</summary>
    Task SaveAsync(WhatIfRun run, CancellationToken ct = default);

    Task<WhatIfRun?> GetAsync(Guid runId, CancellationToken ct = default);
}
