namespace Bazaar.Server.Fun.Mirror;

/// <summary>
/// Persistence interface for the Mirror Agent (docs/design/26-fun-systems.md §5, <c>mirror_signal</c> and <c>mirror_profile</c>).
/// </summary>
public interface IMirrorStore
{
    /// <summary>Records an observed owner decision signal.</summary>
    Task RecordSignalAsync(MirrorSignal signal, CancellationToken ct = default);

    /// <summary>Lists recorded signals, optionally filtered by kind.</summary>
    Task<IReadOnlyList<MirrorSignal>> ListSignalsAsync(string? kind = null, CancellationToken ct = default);

    /// <summary>Saves a versioned profile.</summary>
    Task SaveProfileAsync(MirrorProfile profile, CancellationToken ct = default);

    /// <summary>Gets a profile by version (latest if null).</summary>
    Task<MirrorProfile?> GetProfileAsync(int? version = null, CancellationToken ct = default);

    /// <summary>Gets the next version number for profile generation.</summary>
    Task<int> GetNextProfileVersionAsync(CancellationToken ct = default);

    /// <summary>Owner control: wipes the profile, rules, and reset learned state.</summary>
    Task WipeProfileAndEmbeddingsAsync(CancellationToken ct = default);

    /// <summary>Owner control: deletes an individual learned rule.</summary>
    Task<bool> DeleteRuleAsync(string ruleId, CancellationToken ct = default);
}
