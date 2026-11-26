using Bazaar.Client.Chat.Abstractions;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Chat;

/// <summary>
/// Who is typing in one group chat, fed from the wire's ephemeral typing deltas (docs/design/02, "Typing signal"). A signal lives
/// <c>TtlMs</c> after it arrives unless refreshed; a stop, or a message from that person (<see cref="Clear"/>), ends it sooner.
/// Nothing is stored. Thread-safe; <see cref="Changed"/> is raised on the calling or timer thread, outside the lock.
/// </summary>
public sealed class WireChatTypingSource : IChatTypingSource, IDisposable
{
    private readonly Guid _gcId;
    private readonly Func<Guid, string> _nameOf;
    private readonly TimeProvider _time;
    private readonly List<Typer> _typers = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <param name="gcId">The group chat; signals for other chats are ignored.</param>
    /// <param name="nameOf">Display name of a principal.</param>
    /// <param name="time">Clock for the expiry timers.</param>
    public WireChatTypingSource(Guid gcId, Func<Guid, string> nameOf, TimeProvider? time = null)
    {
        _gcId = gcId;
        _nameOf = nameOf ?? throw new ArgumentNullException(nameof(nameOf));
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<string> Typers
    {
        get
        {
            lock (_gate) return [.. _typers.Select(t => _nameOf(t.Id))];
        }
    }

    /// <summary>Applies one typing delta: a start or refresh restarts the person's expiry, a stop removes them.</summary>
    public void Apply(PresenceTyping signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.GcId != _gcId)
            return;
        if (!signal.Typing)
        {
            Clear(signal.PrincipalId);
            return;
        }

        var changed = false;
        lock (_gate)
        {
            if (_disposed)
                return;
            var ttl = TimeSpan.FromMilliseconds(Math.Max(1, signal.TtlMs));
            var at = _typers.FindIndex(t => t.Id == signal.PrincipalId);
            if (at < 0)
            {
                _typers.Add(new Typer(signal.PrincipalId, _time.CreateTimer(Expire, signal.PrincipalId, ttl, Timeout.InfiniteTimeSpan)));
                changed = true;
            }
            else
            {
                _typers[at].Timer.Change(ttl, Timeout.InfiniteTimeSpan);
            }
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a person at once, such as when their message arrives. No-op when they were not typing.</summary>
    public void Clear(Guid principalId)
    {
        var removed = false;
        lock (_gate)
        {
            var at = _typers.FindIndex(t => t.Id == principalId);
            if (at >= 0)
            {
                _typers[at].Timer.Dispose();
                _typers.RemoveAt(at);
                removed = true;
            }
        }

        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var typer in _typers)
                typer.Timer.Dispose();
            _typers.Clear();
        }
    }

    private void Expire(object? state) => Clear((Guid)state!);

    private sealed record Typer(Guid Id, ITimer Timer);
}
