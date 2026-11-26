using Bazaar.Constellation.Rendering.Core;
using Xunit;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class NodeLodPolicyTests
{
    public static TheoryData<NodeKind> Kinds => new(Enum.GetValues<NodeKind>());

    [Theory]
    [MemberData(nameof(Kinds))]
    public void SmallerOnScreen_NeverGivesMoreDetail(NodeKind kind)
    {
        foreach (float detail in new[] { 0.1f, 1f, 2f })
        {
            NodeDetailLevel last = NodeDetailLevel.L0;
            for (float px = 1000f; px >= 0f; px -= 0.5f)
            {
                NodeDetailLevel now = NodeLodPolicy.Pick(kind, px, detail);
                Assert.True(now >= last);
                last = now;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Hysteresis_HoldsRungAroundACutoff(NodeKind kind)
    {
        float cut = NodeLodPolicy.Cutoff(kind, NodeDetailLevel.L1, 1f);

        // Jitter within the margin around the L1/L2 cutoff must not change the rung.
        NodeDetailLevel level = NodeDetailLevel.L1;
        for (int i = 0; i < 50; i++)
        {
            float px = cut * (i % 2 == 0 ? 1.05f : 0.95f);
            level = NodeLodPolicy.Pick(kind, px, 1f, level);
            Assert.Equal(NodeDetailLevel.L1, level);
        }

        // Clearly past the margin it does change.
        Assert.Equal(NodeDetailLevel.L2, NodeLodPolicy.Pick(kind, cut * 0.8f, 1f, NodeDetailLevel.L1));
        Assert.Equal(NodeDetailLevel.L1, NodeLodPolicy.Pick(kind, cut * 1.2f, 1f, NodeDetailLevel.L2));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void LowerDetail_PullsCutoffsIn(NodeKind kind)
    {
        foreach (NodeDetailLevel boundary in new[] { NodeDetailLevel.L0, NodeDetailLevel.L1, NodeDetailLevel.L2, NodeDetailLevel.L3 })
        {
            Assert.True(NodeLodPolicy.Cutoff(kind, boundary, 0.5f) > NodeLodPolicy.Cutoff(kind, boundary, 1f));
            Assert.True(NodeLodPolicy.Cutoff(kind, boundary, 2f) < NodeLodPolicy.Cutoff(kind, boundary, 1f));
        }

        float px = NodeLodPolicy.Cutoff(kind, NodeDetailLevel.L1, 1f) * 1.01f;
        Assert.True(NodeLodPolicy.Pick(kind, px, 0.5f) > NodeLodPolicy.Pick(kind, px, 1f));
    }

    [Fact]
    public void ControllerOverBudget_SimplifiesNodes()
    {
        var c = new FrameBudgetController(RenderTiers.Mid, 3.3);
        NodeDetailLevel before = NodeLodPolicy.Pick(NodeKind.Document, 130f, c.Detail);
        for (int i = 0; i < 20; i++)
        {
            c.Update(5.0);
        }

        Assert.True(NodeLodPolicy.Pick(NodeKind.Document, 130f, c.Detail) > before);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void FarRung_IsADotForEveryKind(NodeKind kind)
    {
        Assert.Equal(NodeDetailLevel.L4, NodeLodPolicy.Pick(kind, 0.5f, 1f));
        Assert.Equal(NodeContent.Dot, NodeLodPolicy.ContentFor(kind, NodeDetailLevel.L4));
        Assert.Equal(NodeContent.Marker, NodeLodPolicy.ContentFor(kind, NodeDetailLevel.L3));
    }

    [Fact]
    public void NoteInSpace_ShowsTextOnlyAtTopRungs()
    {
        Assert.True(NodeLodPolicy.ShowsText(NodeKind.NoteInSpace, NodeDetailLevel.L0));
        Assert.True(NodeLodPolicy.ShowsText(NodeKind.NoteInSpace, NodeDetailLevel.L1));
        Assert.False(NodeLodPolicy.ShowsText(NodeKind.NoteInSpace, NodeDetailLevel.L2));
        Assert.False(NodeLodPolicy.ShowsText(NodeKind.NoteInSpace, NodeDetailLevel.L3));
        Assert.False(NodeLodPolicy.ShowsText(NodeKind.NoteInSpace, NodeDetailLevel.L4));
        Assert.True(NodeLodPolicy.ContentFor(NodeKind.Document, NodeDetailLevel.L0).HasFlag(NodeContent.Text));
        Assert.False(NodeLodPolicy.ContentFor(NodeKind.Document, NodeDetailLevel.L1).HasFlag(NodeContent.Text));
    }

    [Fact]
    public void BadDetail_IsClamped()
    {
        Assert.Equal(NodeLodPolicy.Pick(NodeKind.Idea, 50f, 1f), NodeLodPolicy.Pick(NodeKind.Idea, 50f, float.NaN));
        Assert.Equal(NodeLodPolicy.Pick(NodeKind.Idea, 50f, FrameBudgetController.MinDetail), NodeLodPolicy.Pick(NodeKind.Idea, 50f, -3f));
    }
}
