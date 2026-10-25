using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Testing;

/// <summary>
/// A controllable <see cref="TimeProvider"/> for tests. Wraps <see cref="FakeTimeProvider"/> so code that takes a
/// <see cref="TimeProvider"/> sees one clock, and timers fire only when the test advances time.
/// </summary>
public sealed class FakeClock : TimeProvider
{
    /// <summary>The start instant when none is given. Fixed, so logged clock values are comparable across runs.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _inner;

    public FakeClock(DateTimeOffset? start = null, TimeZoneInfo? localTimeZone = null)
    {
        _inner = new FakeTimeProvider(start ?? DefaultStart);
        if (localTimeZone is not null)
        {
            _inner.SetLocalTimeZone(localTimeZone);
        }
    }

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _inner.CreateTimer(callback, state, dueTime, period);

    /// <summary>Moves the clock forward and fires any timers that come due.</summary>
    public void Advance(TimeSpan by) => _inner.Advance(by);

    /// <summary>Sets the UTC instant. Timers are not fired; use <see cref="Advance"/> to move time forward.</summary>
    public void SetUtcNow(DateTimeOffset utcNow) => _inner.SetUtcNow(utcNow);

    public void SetLocalTimeZone(TimeZoneInfo localTimeZone) => _inner.SetLocalTimeZone(localTimeZone);

    public override string ToString() => $"FakeClock(utc={GetUtcNow():O}, local={LocalTimeZone.Id})";
}
