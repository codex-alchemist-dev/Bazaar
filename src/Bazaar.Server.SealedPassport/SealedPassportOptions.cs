namespace Bazaar.Server.SealedPassport;

/// <summary>
/// Operator settings of the sealed Passport (doc 61 WP4; configurability rule: protective defaults). Sealing itself is not a setting.
/// </summary>
public sealed record SealedPassportOptions
{
    public const string SectionName = "Bazaar:SealedPassport";

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
