using Bazaar.Server.Presence.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Presence.Tests;

/// <summary>A publisher that records what it was asked to publish.</summary>
internal sealed class RecordingPublisher : IPresencePublisher
{
    public List<PresenceRow> Published { get; } = [];

    public bool Throw { get; set; }

    public ValueTask PublishDeltaAsync(PresenceRow row, CancellationToken ct)
    {
        if (Throw)
        {
            throw new InvalidOperationException("socket gone");
        }

        lock (Published) Published.Add(row);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<PresenceRow>> GetSnapshotAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PresenceRow>>([]);
}

/// <summary>A projector over an in-memory store, a recording publisher and a fake clock.</summary>
internal sealed class PresenceRig
{
    public static readonly Guid Ada = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    public static readonly Guid Bob = Guid.Parse("00000000-0000-7000-8000-00000000000b");

    public PresenceRig(PresenceOptions? options = null, IPresenceStore? store = null)
    {
        Options = options ?? new PresenceOptions();
        Store = store ?? new InMemoryPresenceStore();
        Projector = new ActivityProjector(Store, Publisher, Time, Options);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));

    public PresenceOptions Options { get; }

    public IPresenceStore Store { get; }

    public RecordingPublisher Publisher { get; } = new();

    public ActivityProjector Projector { get; }

    /// <summary>A source time <paramref name="seconds"/> after the clock's start.</summary>
    public static DateTimeOffset At(int seconds) => new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    public ValueTask Send(ActivityEvent e) => Projector.OnEventAsync(e, TestContext.Current.CancellationToken);

    public async Task<PresenceRow> RowAsync(Guid agent) =>
        await Store.GetAsync(agent, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("no row");
}
