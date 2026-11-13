using Bazaar.Server.Modules.Releases;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Releases;

/// <summary>Release candidate status transitions; Halt is terminal. Time comes from a fake TimeProvider.</summary>
public sealed class ReleaseCandidateLifecycleTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(_start);
    private readonly ReleaseCandidateLifecycle _lifecycle;

    public ReleaseCandidateLifecycleTests()
    {
        _lifecycle = new ReleaseCandidateLifecycle(_clock);
    }

    private ReleaseCandidate Propose(string version = "0.1.0", ReleaseChannel channel = ReleaseChannel.Beta) =>
        _lifecycle.Propose(version, channel, []);

    [Fact]
    public void Proposed_candidate_starts_on_canary_in_candidate_status_with_the_clock_time()
    {
        var candidate = Propose();

        Assert.Equal(ReleaseCandidateStatus.Candidate, candidate.Status);
        Assert.Equal(ReleaseRing.Canary, candidate.Ring);
        Assert.Equal(_start, candidate.StatusChangedAt);
        Assert.Null(candidate.HaltReason);
    }

    [Fact]
    public void Start_moves_candidate_to_rolling_and_stamps_the_time()
    {
        _clock.Advance(TimeSpan.FromMinutes(5));

        var rolling = _lifecycle.Start(Propose());

        Assert.Equal(ReleaseCandidateStatus.Rolling, rolling.Status);
        Assert.Equal(_start.AddMinutes(5), rolling.StatusChangedAt);
    }

    [Fact]
    public void Start_twice_is_refused()
    {
        var rolling = _lifecycle.Start(Propose());

        Assert.Throws<InvalidOperationException>(() => _lifecycle.Start(rolling));
    }

    [Fact]
    public void Continue_gates_walk_canary_to_early_to_broad_then_complete()
    {
        var candidate = _lifecycle.Start(Propose());

        candidate = _lifecycle.ApplyGate(candidate, GateDecision.Continue);
        Assert.Equal((ReleaseCandidateStatus.Rolling, ReleaseRing.Early), (candidate.Status, candidate.Ring));

        candidate = _lifecycle.ApplyGate(candidate, GateDecision.Continue);
        Assert.Equal((ReleaseCandidateStatus.Rolling, ReleaseRing.Broad), (candidate.Status, candidate.Ring));

        candidate = _lifecycle.ApplyGate(candidate, GateDecision.Continue);
        Assert.Equal(ReleaseCandidateStatus.Complete, candidate.Status);
        Assert.Equal(ReleaseRing.Broad, candidate.Ring);
    }

    [Fact]
    public void Hold_leaves_the_candidate_unchanged()
    {
        var rolling = _lifecycle.Start(Propose());

        Assert.Equal(rolling, _lifecycle.ApplyGate(rolling, GateDecision.Hold));
    }

    [Fact]
    public void Gate_halt_halts_the_rolling_ring_with_a_recorded_reason()
    {
        var rolling = _lifecycle.Start(Propose());

        var halted = _lifecycle.ApplyGate(rolling, GateDecision.Halt);

        Assert.Equal(ReleaseCandidateStatus.Halted, halted.Status);
        Assert.Equal("Health gate halted the Canary ring.", halted.HaltReason);
    }

    [Fact]
    public void Halt_is_allowed_from_candidate_and_from_rolling()
    {
        var proposed = Propose();
        var rolling = _lifecycle.Start(Propose("0.2.0"));

        Assert.Equal(ReleaseCandidateStatus.Halted, _lifecycle.Halt(proposed, "owner stop").Status);
        Assert.Equal(ReleaseCandidateStatus.Halted, _lifecycle.Halt(rolling, "owner stop").Status);
    }

    [Fact]
    public void Halt_requires_a_reason()
    {
        Assert.ThrowsAny<ArgumentException>(() => _lifecycle.Halt(Propose(), "  "));
    }

    [Fact]
    public void Halted_candidate_cannot_be_started_advanced_held_completed_or_halted_again()
    {
        var halted = _lifecycle.Halt(_lifecycle.Start(Propose()), "red");

        Assert.Throws<InvalidOperationException>(() => _lifecycle.Start(halted));
        Assert.Throws<InvalidOperationException>(() => _lifecycle.ApplyGate(halted, GateDecision.Continue));
        Assert.Throws<InvalidOperationException>(() => _lifecycle.ApplyGate(halted, GateDecision.Hold));
        Assert.Throws<InvalidOperationException>(() => _lifecycle.ApplyGate(halted, GateDecision.Halt));
        Assert.Throws<InvalidOperationException>(() => _lifecycle.Halt(halted, "again"));
    }

    [Fact]
    public void Gate_halt_of_an_already_halted_candidate_is_refused_and_keeps_the_reason()
    {
        var halted = _lifecycle.ApplyGate(_lifecycle.Start(Propose()), GateDecision.Halt);

        Assert.Throws<InvalidOperationException>(() => _lifecycle.ApplyGate(halted, GateDecision.Halt));
        Assert.Equal("Health gate halted the Canary ring.", halted.HaltReason);
    }

    [Fact]
    public void Proposed_candidate_cannot_pass_a_gate_before_it_starts()
    {
        var proposed = Propose();

        Assert.Throws<InvalidOperationException>(() => _lifecycle.ApplyGate(proposed, GateDecision.Continue));
        Assert.Throws<InvalidOperationException>(() => _lifecycle.ApplyGate(proposed, GateDecision.Hold));
    }

    [Fact]
    public void Complete_candidate_is_terminal()
    {
        var complete = _lifecycle.Start(Propose());
        complete = _lifecycle.ApplyGate(complete, GateDecision.Continue);
        complete = _lifecycle.ApplyGate(complete, GateDecision.Continue);
        complete = _lifecycle.ApplyGate(complete, GateDecision.Continue);

        Assert.Throws<InvalidOperationException>(() => _lifecycle.ApplyGate(complete, GateDecision.Continue));
        Assert.Throws<InvalidOperationException>(() => _lifecycle.Halt(complete, "late"));
    }

    [Fact]
    public void Propose_refuses_a_version_that_was_halted_on_any_channel()
    {
        var halted = _lifecycle.Halt(_lifecycle.Start(Propose("0.3.0", ReleaseChannel.Beta)), "red");

        Assert.Throws<InvalidOperationException>(() => _lifecycle.Propose("0.3.0", ReleaseChannel.Beta, [halted]));
        Assert.Throws<InvalidOperationException>(() => _lifecycle.Propose("0.3.0", ReleaseChannel.Nightly, [halted]));
    }

    [Fact]
    public void Propose_allows_a_version_that_was_not_halted()
    {
        var halted = _lifecycle.Halt(_lifecycle.Start(Propose("0.3.0")), "red");
        var rolling = _lifecycle.Start(Propose("0.4.0"));

        var next = _lifecycle.Propose("0.4.1", ReleaseChannel.Beta, [halted, rolling]);

        Assert.Equal("0.4.1", next.Version);
        Assert.Equal(ReleaseCandidateStatus.Candidate, next.Status);
    }

    [Fact]
    public void Halt_is_terminal_even_when_the_clock_moves_on()
    {
        var halted = _lifecycle.Halt(Propose(), "red");
        _clock.Advance(TimeSpan.FromDays(3));

        Assert.Throws<InvalidOperationException>(() => _lifecycle.Start(halted));
        Assert.Equal(_start, halted.StatusChangedAt);
    }

    [Fact]
    public void Constructor_and_inputs_reject_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new ReleaseCandidateLifecycle(null!));
        Assert.Throws<ArgumentNullException>(() => _lifecycle.Start(null!));
        Assert.Throws<ArgumentNullException>(() => _lifecycle.Propose("0.1.0", ReleaseChannel.Beta, null!));
    }
}
