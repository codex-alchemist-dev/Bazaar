using Bazaar.Server.Council;
using Bazaar.Server.Council.Prompts;

namespace Bazaar.Server.Tests.Council.Prompts;

public class PrivacyGuardTests
{
    private const string _peerReason = "The rota adds review load without a clear gain.";

    private static readonly string[] _peerIds = ["agent-2", "agent-3"];

    private static PrivacyCheckRequest Request(
        string prompt,
        BallotPhase phase = BallotPhase.Independent,
        PromptContext context = PromptContext.Working,
        bool revealed = false,
        IReadOnlyCollection<string>? reasons = null) =>
        new(prompt, phase, context, revealed, _peerIds, reasons ?? []);

    [Fact]
    public void Independent_prompt_with_a_peer_id_is_denied()
    {
        var result = PrivacyGuard.Check(Request("Vote now. agent-2 has already voted."));

        Assert.IsType<PrivacyCheckResult.Denied>(result);
    }

    [Fact]
    public void Independent_prompt_with_revealed_reasoning_is_denied()
    {
        var result = PrivacyGuard.Check(Request($"Motion text. {_peerReason}", reasons: [_peerReason]));

        var denied = Assert.IsType<PrivacyCheckResult.Denied>(result);
        Assert.Contains("reasoning", denied.Reason);
    }

    [Fact]
    public void Clean_independent_prompt_is_allowed()
    {
        var result = PrivacyGuard.Check(Request("Motion: adopt the rota. Choices: yes, no, abstain."));

        Assert.IsType<PrivacyCheckResult.Allowed>(result);
    }

    [Fact]
    public void Thinking_prompt_with_reasoning_before_reveal_is_denied()
    {
        var result = PrivacyGuard.Check(Request(
            $"Thinking GC. Peer said: {_peerReason}",
            context: PromptContext.Thinking,
            revealed: false,
            reasons: [_peerReason]));

        var denied = Assert.IsType<PrivacyCheckResult.Denied>(result);
        Assert.Contains("Thinking", denied.Reason);
    }

    [Fact]
    public void Thinking_prompt_without_reasoning_before_reveal_is_allowed()
    {
        var result = PrivacyGuard.Check(Request("Thinking GC. Motion only.", context: PromptContext.Thinking));

        Assert.IsType<PrivacyCheckResult.Allowed>(result);
    }

    [Fact]
    public void Working_follow_up_after_reveal_with_reasons_is_allowed()
    {
        var result = PrivacyGuard.Check(Request(
            $"Reason A: {_peerReason}",
            phase: BallotPhase.FollowUp,
            context: PromptContext.Working,
            revealed: true,
            reasons: [_peerReason]));

        Assert.IsType<PrivacyCheckResult.Allowed>(result);
    }

    [Fact]
    public void Thinking_follow_up_after_reveal_with_reasons_is_allowed()
    {
        var result = PrivacyGuard.Check(Request(
            $"Reason A: {_peerReason}",
            phase: BallotPhase.FollowUp,
            context: PromptContext.Thinking,
            revealed: true,
            reasons: [_peerReason]));

        Assert.IsType<PrivacyCheckResult.Allowed>(result);
    }

    [Fact]
    public void Follow_up_prompt_before_reveal_is_denied()
    {
        var result = PrivacyGuard.Check(Request("Follow-up.", phase: BallotPhase.FollowUp, revealed: false));

        Assert.IsType<PrivacyCheckResult.Denied>(result);
    }

    [Fact]
    public void Follow_up_prompt_with_a_peer_id_is_denied()
    {
        var result = PrivacyGuard.Check(Request(
            "Reason A: agent-3 wrote this.",
            phase: BallotPhase.FollowUp,
            revealed: true));

        Assert.IsType<PrivacyCheckResult.Denied>(result);
    }

    [Fact]
    public void String_check_denies_a_prompt_containing_a_forbidden_snippet()
    {
        var result = PrivacyGuard.EnsureNoPeerBallotLeak("Vote text with agent-2 inside.", ["agent-2"]);

        Assert.IsType<PrivacyCheckResult.Denied>(result);
    }

    [Fact]
    public void String_check_allows_a_prompt_without_forbidden_snippets_and_ignores_blank_ones()
    {
        var result = PrivacyGuard.EnsureNoPeerBallotLeak("Clean motion text.", ["", "  ", "agent-9"]);

        Assert.IsType<PrivacyCheckResult.Allowed>(result);
    }

    [Fact]
    public void Ensure_allowed_throws_with_the_denial_reason()
    {
        var denied = new PrivacyCheckResult.Denied("Prompt contains a peer voter id.");

        var exception = Assert.Throws<CouncilPrivacyViolationException>(() => PrivacyGuard.EnsureAllowed(denied));

        Assert.Equal("Prompt contains a peer voter id.", exception.Reason);
    }

    [Fact]
    public void Ensure_allowed_passes_an_allowed_result()
    {
        PrivacyGuard.EnsureAllowed(new PrivacyCheckResult.Allowed());
    }
}
