using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Sanitization;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.RedTeam;

/// <summary>One engine decision for one case under one session and one grant.</summary>
/// <param name="Session">Which session the call ran in: <c>applied</c>, <c>clean</c> or <c>hard</c>.</param>
/// <param name="Grant">Which grant was presented: <c>none</c> or <c>fake</c> (a grant whose hash cannot match).</param>
/// <param name="ReachedTier">The tier the call reaches in that session, or null for an unregistered tool.</param>
/// <param name="Decision">What the engine answered.</param>
public sealed record ReplayRun(string Session, string Grant, ToolTier? ReachedTier, PolicyDecision Decision);

/// <summary>Everything the replay produced for one case.</summary>
/// <param name="Case">The case replayed.</param>
/// <param name="SourceId">Deterministic id the content carries through the taint rules.</param>
/// <param name="Applied">The session after the real <see cref="TaintRules.Apply"/>, from a clean start.</param>
/// <param name="Sanitized">The content after the real <see cref="TextSanitizer"/>.</param>
/// <param name="Runs">One entry per session and grant combination.</param>
public sealed record ReplayResult(
    RedTeamCase Case,
    Guid SourceId,
    SessionTaint Applied,
    SanitizedText Sanitized,
    IReadOnlyList<ReplayRun> Runs);

/// <summary>
/// Replays corpus cases through the real <see cref="TaintRules"/>, <see cref="TextSanitizer"/> and
/// <see cref="ToolPolicyEngine"/>. Deterministic: fixed time, fixed ids, no I/O.
/// </summary>
public static class RedTeamReplay
{
    /// <summary>The fixed clock every replay uses.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The agent the replayed calls belong to.</summary>
    public const string AgentId = "agent.redteam";

    private static readonly Guid _sessionId = new("00000000-0000-0000-0000-000000000012");
    private static readonly TextSanitizer _sanitizer = new();

    /// <summary>
    /// Runs the case's content through the taint rules, sanitizes it, then asks the engine about the case's tool call in
    /// three sessions (the applied one, a clean one and a hard-tainted one), each with no grant and with a fake grant.
    /// </summary>
    public static ReplayResult Run(RedTeamCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        var sourceId = SourceIdFor(testCase.Id);
        var trust = Enum.Parse<ContentTrust>(testCase.Trust);
        var sourceState = Enum.Parse<TaintState>(testCase.SourceState);
        var input = new TaintInput(sourceId, trust, sourceState, testCase.InheritsExternal);

        var applied = TaintRules.Apply(SessionTaint.Clean, input, TaintPolicy.Default);
        var hard = new SessionTaint(
            TaintState.Hard,
            ImmutableArray.Create(new TaintSource(sourceId, ContentTrust.External, TaintState.Hard)));
        var sessions = new (string Label, SessionTaint Taint)[]
        {
            ("applied", applied),
            ("clean", SessionTaint.Clean),
            ("hard", hard),
        };

        var engine = new ToolPolicyEngine(RedTeamTools.Registry, new InMemoryRateBucketStore(), PolicyOptions.Default);
        var role = Enum.Parse<SessionRoles>(testCase.Role);
        var tool = testCase.ToolCall.Tool;
        var runs = new List<ReplayRun>();

        foreach (var (label, taint) in sessions)
        {
            foreach (var grantLabel in new[] { "none", "fake" })
            {
                var context = new DispatchContext(
                    AgentId,
                    _sessionId,
                    role,
                    tool,
                    Platform: null,
                    Target: testCase.ToolCall.Target,
                    Args: testCase.ToolCall.Args,
                    Taint: taint,
                    Now: Now,
                    Grant: grantLabel == "fake" ? FakeGrant(tool) : null);

                runs.Add(new ReplayRun(label, grantLabel, ReachedTier(tool, taint), engine.Evaluate(context)));
            }
        }

        var sanitized = _sanitizer.Sanitize(testCase.Content, SensitiveTerms.Empty);
        return new ReplayResult(testCase, sourceId, applied, sanitized, runs);
    }

    /// <summary>
    /// The tier a call to <paramref name="tool"/> reaches in a session. Tier 1 escalates to Tier 2 under hard taint, as doc 15
    /// requires; the replay computes it independently so the invariant does not depend on the engine's own reason codes.
    /// </summary>
    public static ToolTier? ReachedTier(string tool, SessionTaint taint)
    {
        if (!RedTeamTools.Registry.TryGet(tool, out var capability))
        {
            return null;
        }

        var tier = capability.Tier;
        if (tier == ToolTier.AutonomousWithNotice && taint.State == TaintState.Hard)
        {
            tier = ToolTier.OwnerApproves;
        }

        return tier;
    }

    /// <summary>
    /// A grant that names the right agent and tool but carries an all-zero hash. It cannot match any real dispatch hash,
    /// so the engine must treat it as no approval.
    /// </summary>
    public static VerifiedApproval FakeGrant(string tool) =>
        new(Guid.Empty, AgentId, tool, new byte[DispatchHash.Length], Now.AddDays(1));

    /// <summary>A stable id for a case, derived from its id, so replays and failure messages agree.</summary>
    public static Guid SourceIdFor(string caseId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("redteam:" + caseId)).AsSpan(0, 16));
}
