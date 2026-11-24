namespace Bazaar.Client.Athanor.Tests.Drafts;

/// <summary>A clock the test moves by hand, so saved-at ordering is exact.</summary>
public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
