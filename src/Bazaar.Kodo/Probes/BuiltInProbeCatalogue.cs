namespace Bazaar.Kodo.Probes;

/// <summary>
/// One row of the built-in probe catalogue (doc 46 section 9.1). Criticality, cadence and pool come from the doc table.
/// <see cref="Critical"/> is false for families that are critical only in one case; the server sets that case per instance.
/// </summary>
/// <param name="Id">The probe id, or its family prefix ending in a dot for per-instance probes.</param>
/// <param name="Critical">True when the probe can make the state failing.</param>
/// <param name="Cadence">How often the probe runs.</param>
/// <param name="Pool">The bulkhead pool the probe runs in (section 7.3).</param>
public sealed record ProbeCatalogueEntry(string Id, bool Critical, TimeSpan Cadence, string Pool);

/// <summary>The built-in probes this slice implements, with their doc 46 section 9.1 rows.</summary>
public static class BuiltInProbeCatalogue
{
    /// <summary>The catalogue rows, in doc order.</summary>
    public static IReadOnlyList<ProbeCatalogueEntry> Entries { get; } =
    [
        new("host.disk.free.", Critical: false, TimeSpan.FromMinutes(1), "local"),
        new("host.memory.pressure", Critical: false, TimeSpan.FromMinutes(1), "local"),
        new("host.clock.drift", Critical: true, TimeSpan.FromMinutes(5), "external"),
        new("kodo.self.journal_writable", Critical: true, TimeSpan.FromSeconds(30), "local"),
        new("kodo.self.spill", Critical: false, TimeSpan.FromMinutes(1), "local"),
    ];
}
