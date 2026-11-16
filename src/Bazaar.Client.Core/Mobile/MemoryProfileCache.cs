namespace Bazaar.Client.Core.Mobile;

/// <summary>An in-memory <see cref="IProfileCache"/>. It lasts for the process only, which suits tests and previews.</summary>
public sealed class MemoryProfileCache : IProfileCache
{
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private CachedProfile? _cached;

    /// <param name="clock">Source of the save time. Null uses the system clock.</param>
    public MemoryProfileCache(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public CachedProfile? Load()
    {
        lock (_gate)
        {
            return _cached;
        }
    }

    /// <inheritdoc />
    public void Save(PhoneProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var copy = new PhoneProfile(profile.Revision, profile.PaneKinds.ToArray());
        lock (_gate)
        {
            _cached = new CachedProfile(copy, _clock.GetUtcNow());
        }
    }
}
