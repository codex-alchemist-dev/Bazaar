namespace Bazaar.Server.SealedPassport;

/// <summary>
/// Operator settings of the sealed Passport (doc 61 WP4; configurability rule: protective defaults). Sealing itself is not a setting.
/// </summary>
public sealed record SealedPassportOptions
{
    public const string SectionName = "Bazaar:SealedPassport";

    /// <summary>
    /// Whether Chromium profile folders may exist in plaintext at all (owner ruling 2026-11-22: only under the verified <see cref="MountRoot"/>, only
    /// while a browser runs; a documented exception to doc 61 invariant 1). Default false: opening a plaintext profile folder is refused and audited
    /// unless this is true AND <see cref="MountRoot"/> is set and verified.
    /// </summary>
    public bool AllowPlaintextProfileFolders { get; init; }

    /// <summary>
    /// The folder under which plaintext Chromium profile folders may exist while open. Must be tmpfs or the doc 63 encrypted volume, confirmed by
    /// the registered mount-root verifier. Null (the default) means no plaintext profile folder can be opened at all.
    /// </summary>
    public string? MountRoot { get; init; }

    /// <summary>How long an open waits for the profile folder's scope lease (default 30 seconds).</summary>
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Folder names inside a Chromium profile that are never sealed (caches that Chromium rebuilds). Matched against any path segment, ordinal.
    /// Leaving them out keeps less data at rest and keeps a profile inside the store's bulk limit.
    /// </summary>
    public IReadOnlyList<string> ExcludedFolders { get; init; } =
        ["Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Crashpad", "component_crx_cache"];
}
