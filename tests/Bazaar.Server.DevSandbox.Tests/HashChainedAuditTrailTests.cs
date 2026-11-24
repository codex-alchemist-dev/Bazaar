namespace Bazaar.Server.DevSandbox.Tests;

public class HashChainedAuditTrailTests
{
    private const string _zeros = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly DateTimeOffset _start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyTrailVerifiesAndHeadIsSixtyFourZeros()
    {
        var trail = new HashChainedAuditTrail();

        Assert.True(trail.Verify());
        Assert.Empty(trail.Events);
        Assert.Equal(_zeros, trail.HeadHash);
        Assert.True(HashChainedAuditTrail.VerifyEvents([]));
    }

    [Fact]
    public void ChainLinksEachEventToThePrevious()
    {
        var trail = new HashChainedAuditTrail(new FixedTimeProvider(_start));

        var first = trail.Append(Action("net.egress", "api.test.local", ""), SandboxDecision.Denied, "egress-not-allowed");
        var second = trail.Append(Action("vault.read", "notes/a.md", ""), SandboxDecision.Allowed, "granted");
        var third = trail.Append(Action("net.egress", "api.test.local", "body"), SandboxDecision.Denied, "canary");

        var events = trail.Events;
        Assert.Equal(3, events.Count);
        Assert.Equal([0L, 1L, 2L], events.Select(e => e.Sequence));
        Assert.Equal(_zeros, first.PreviousHash);
        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.Equal(second.Hash, third.PreviousHash);
        Assert.Equal(third.Hash, trail.HeadHash);
        Assert.True(trail.Verify());
        Assert.True(HashChainedAuditTrail.VerifyEvents(events));
    }

    [Fact]
    public void HashesAreLowercaseHexOfSixtyFourCharacters()
    {
        var trail = new HashChainedAuditTrail(new FixedTimeProvider(_start));

        var appended = trail.Append(Action("syscall", "", ""), SandboxDecision.Allowed, "granted");

        Assert.Matches("^[0-9a-f]{64}$", appended.Hash);
    }

    [Theory]
    [InlineData("Decision")]
    [InlineData("Reason")]
    [InlineData("Target")]
    [InlineData("Payload")]
    [InlineData("Sequence")]
    [InlineData("At")]
    [InlineData("PreviousHash")]
    public void TamperingWithAnySingleFieldIsDetected(string field)
    {
        var events = BuildChain(3);
        var tampered = events.ToArray();
        tampered[1] = Mutate(tampered[1], field);

        Assert.False(HashChainedAuditTrail.VerifyEvents(tampered));
    }

    [Fact]
    public void UntamperedChainOfThreeVerifies()
    {
        var events = BuildChain(3);

        Assert.True(HashChainedAuditTrail.VerifyEvents(events));
    }

    [Fact]
    public void ReorderingTwoEventsIsDetected()
    {
        var events = BuildChain(4);
        var swapped = new List<SandboxAuditEvent> { events[0], events[2], events[1], events[3] };

        Assert.False(HashChainedAuditTrail.VerifyEvents(swapped));
    }

    [Fact]
    public void DroppingAMiddleEventIsDetected()
    {
        var events = BuildChain(4);
        var dropped = new List<SandboxAuditEvent> { events[0], events[2], events[3] };

        Assert.False(HashChainedAuditTrail.VerifyEvents(dropped));
    }

    [Fact]
    public void DeterministicHashForFixedTime()
    {
        var first = new HashChainedAuditTrail(new FixedTimeProvider(_start));
        var second = new HashChainedAuditTrail(new FixedTimeProvider(_start));

        foreach (var trail in new[] { first, second })
        {
            trail.Append(Action("net.egress", "api.test.local", "payload"), SandboxDecision.Denied, "no-grant");
            trail.Append(Action("file", "/vault/a.md", ""), SandboxDecision.Allowed, "granted");
        }

        Assert.Equal(first.HeadHash, second.HeadHash);
        Assert.Equal(first.Events[0].Hash, second.Events[0].Hash);
        // Pinned from an independent SHA-256 over the same canonical encoding, so a silent format change fails here.
        Assert.Equal("f30035a35ee0e094165ccea25e0c4df71db29ff1274cad4293397e44b929a053", first.HeadHash);
    }

    [Fact]
    public void ConcurrentAppendsFromEightThreadsFormOneValidChain()
    {
        var trail = new HashChainedAuditTrail();

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, thread =>
        {
            for (var i = 0; i < 100; i++)
            {
                trail.Append(Action("syscall", "", $"t{thread}-{i}"), SandboxDecision.Allowed, "granted");
            }
        });

        var events = trail.Events;
        Assert.Equal(800, events.Count);
        Assert.Equal(Enumerable.Range(0, 800).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.True(trail.Verify());
        Assert.True(HashChainedAuditTrail.VerifyEvents(events));
        Assert.Equal(events[^1].Hash, trail.HeadHash);
    }

    [Fact]
    public void EventsSnapshotDoesNotChangeWhenMoreAreAppended()
    {
        var trail = new HashChainedAuditTrail(new FixedTimeProvider(_start));
        trail.Append(Action("syscall", "", ""), SandboxDecision.Allowed, "granted");
        var snapshot = trail.Events;

        trail.Append(Action("syscall", "", ""), SandboxDecision.Allowed, "granted");

        Assert.Single(snapshot);
        Assert.Equal(2, trail.Events.Count);
    }

    private static SandboxAction Action(string kind, string target, string payload)
    {
        var actionKind = kind switch
        {
            "net.egress" => SandboxActionKind.NetworkAttempt,
            "vault.read" => SandboxActionKind.FileAccess,
            "file" => SandboxActionKind.FileAccess,
            _ => SandboxActionKind.Syscall,
        };
        return new SandboxAction(actionKind, kind, target, payload);
    }

    private static IReadOnlyList<SandboxAuditEvent> BuildChain(int count)
    {
        var trail = new HashChainedAuditTrail(new FixedTimeProvider(_start));
        for (var i = 0; i < count; i++)
        {
            trail.Append(Action("net.egress", $"host-{i}.test.local", $"payload-{i}"), SandboxDecision.Denied, $"reason-{i}");
        }

        return trail.Events;
    }

    private static SandboxAuditEvent Mutate(SandboxAuditEvent e, string field) => field switch
    {
        "Decision" => e with { Decision = e.Decision == SandboxDecision.Allowed ? SandboxDecision.Denied : SandboxDecision.Allowed },
        "Reason" => e with { Reason = e.Reason + "-forged" },
        "Target" => e with { Action = e.Action with { Target = e.Action.Target + ".evil" } },
        "Payload" => e with { Action = e.Action with { Payload = e.Action.Payload + "x" } },
        "Sequence" => e with { Sequence = e.Sequence + 1 },
        "At" => e with { At = e.At.AddMilliseconds(1) },
        "PreviousHash" => e with { PreviousHash = new string('f', 64) },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
