using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc;

public class GcTypeKeysFunTypesTests
{

    [Fact]
    public void GcTypeKeys_has_ward_vent_brainstorm_sidewalk_constants()
    {
        Assert.Equal("ward", GcTypeKeys.Ward);
        Assert.Equal("vent", GcTypeKeys.Vent);
        Assert.Equal("brainstorm", GcTypeKeys.Brainstorm);
        Assert.Equal("sidewalk", GcTypeKeys.Sidewalk);
    }

    [Fact]
    public void IsSideWalk_returns_true_for_sidewalk_type_key()
    {
        var gc = new GcRecord(Guid.NewGuid(), null, GcTypeKeys.Sidewalk, null);
        Assert.True(GcTypeKeys.IsSideWalk(gc));
        Assert.False(GcTypeKeys.IsWard(gc));
    }

    [Fact]
    public void IsWard_returns_true_for_ward_type_key()
    {
        var gc = new GcRecord(Guid.NewGuid(), null, GcTypeKeys.Ward, null);
        Assert.True(GcTypeKeys.IsWard(gc));
        Assert.False(GcTypeKeys.IsSideWalk(gc));
    }

    [Fact]
    public void Sidewalk_GC_does_not_match_thinking_or_dm()
    {
        var gc = new GcRecord(Guid.NewGuid(), null, GcTypeKeys.Sidewalk, null);
        Assert.False(GcTypeKeys.IsThinking(gc));
        Assert.False(GcTypeKeys.IsDm(gc));
    }
}
