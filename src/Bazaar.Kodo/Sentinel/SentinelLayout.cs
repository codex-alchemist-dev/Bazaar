namespace Bazaar.Kodo.Sentinel;

/// <summary>
/// File names the sentinel uses outside the runtime pointer files (doc 46 sections 6.1 and 7.8). The manifest sits inside
/// each slot, so the verifier takes it separately from the slot's other files. The status file lives in the writable data
/// directory, never in the runtime root.
/// </summary>
public static class SentinelLayout
{
    /// <summary>The signed manifest envelope stored in each slot. It is not listed in its own manifest.</summary>
    public const string ManifestFileName = "kodo.manifest.json";

    /// <summary>
    /// A root file written before a pointer swap and removed after the new slot survives probation. It holds the candidate slot id
    /// (PROPOSED name, doc 46 7.8).
    /// </summary>
    public const string ProbationMarkerFileName = "kodo.probation.pending";

    /// <summary>The loopback status file the sentinel writes for the status reader (PROPOSED name, doc 46 7.8).</summary>
    public const string StatusFileName = "kodo.sentinel.status.json";
}
