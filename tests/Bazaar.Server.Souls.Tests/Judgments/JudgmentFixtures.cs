using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Tests.Judgments;

/// <summary>Builders for valid judgment records.</summary>
internal static class JudgmentFixtures
{
    public static readonly Guid SoulId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GcId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AgentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static DecisionRecord Record(Guid? decisionId = null, string title = "Promote Ada to reviewer") => new(
        decisionId ?? Guid.Parse("44444444-4444-4444-4444-444444444444"),
        SoulId,
        7,
        GcId,
        AgentId,
        JudgmentKind.Promotion,
        title,
        [new JudgmentInput(1042, SourceTrust.External), new JudgmentInput(1050, SourceTrust.Owner)],
        ["promote", "hold"],
        "promote",
        "Ada reviewed eight PRs with no reverts and the owner asked for more review load.",
        JudgmentConfidence.Medium,
        new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
}
