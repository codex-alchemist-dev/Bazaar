using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Tests.Security.Policy;

public class CapabilityRegistryTests
{
    private static readonly ToolCapability _tool = new("gc.read", ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Actor);

    public static TheoryData<CapabilityClass> NonReadClasses()
    {
        var data = new TheoryData<CapabilityClass>();
        foreach (var capabilityClass in Enum.GetValues<CapabilityClass>())
        {
            if (capabilityClass != CapabilityClass.Read)
            {
                data.Add(capabilityClass);
            }
        }

        return data;
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var copy = _tool with { Tier = ToolTier.Autonomous };

        Assert.Throws<ArgumentException>(() => CapabilityRegistry.Create([_tool, copy]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" gc.read")]
    [InlineData("gc.read ")]
    public void Blank_or_padded_names_are_rejected(string name)
    {
        var tool = _tool with { Name = name };

        Assert.Throws<ArgumentException>(() => CapabilityRegistry.Create([tool]));
    }

    [Fact]
    public void A_tool_with_no_roles_is_rejected()
    {
        var tool = _tool with { Roles = SessionRoles.None };

        Assert.Throws<ArgumentException>(() => CapabilityRegistry.Create([tool]));
    }

    [Theory]
    [InlineData(ToolTier.Autonomous)]
    [InlineData(ToolTier.AutonomousWithNotice)]
    public void A_merge_tool_below_owner_approves_is_rejected(ToolTier tier)
    {
        var tool = new ToolCapability("repo.merge", tier, CapabilityClass.Merge, SessionRoles.Actor);

        Assert.Throws<ArgumentException>(() => CapabilityRegistry.Create([tool]));
    }

    [Fact]
    public void A_merge_tool_at_owner_approves_is_accepted()
    {
        var tool = new ToolCapability("repo.merge", ToolTier.OwnerApproves, CapabilityClass.Merge, SessionRoles.Actor);

        Assert.NotNull(CapabilityRegistry.Create([tool]));
    }

    [Theory]
    [MemberData(nameof(NonReadClasses))]
    public void A_reader_tool_that_is_not_a_read_class_is_rejected(CapabilityClass capabilityClass)
    {
        var tool = new ToolCapability("outward.tool", ToolTier.OwnerApproves, capabilityClass, SessionRoles.Reader);

        Assert.Throws<ArgumentException>(() => CapabilityRegistry.Create([tool]));
    }

    [Fact]
    public void A_reader_read_tool_is_accepted()
    {
        var tool = new ToolCapability("web.fetch", ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Reader);

        Assert.NotNull(CapabilityRegistry.Create([tool]));
    }

    [Fact]
    public void TryGet_finds_a_tool_by_its_exact_name()
    {
        var registry = CapabilityRegistry.Create([_tool]);

        Assert.True(registry.TryGet("gc.read", out var found));
        Assert.Same(_tool, found);
    }

    [Theory]
    [InlineData("GC.READ")]
    [InlineData("Gc.Read")]
    [InlineData("gc.read ")]
    [InlineData("gc.reads")]
    public void TryGet_does_not_match_a_different_case_or_padded_or_longer_name(string name)
    {
        var registry = CapabilityRegistry.Create([_tool]);

        Assert.False(registry.TryGet(name, out _));
    }

    [Fact]
    public void Names_differing_only_by_case_are_separate_tools()
    {
        var lower = _tool;
        var upper = _tool with { Name = "GC.READ" };
        var registry = CapabilityRegistry.Create([lower, upper]);

        Assert.True(registry.TryGet("gc.read", out var lowerFound));
        Assert.True(registry.TryGet("GC.READ", out var upperFound));
        Assert.Same(lower, lowerFound);
        Assert.Same(upper, upperFound);
    }
}
