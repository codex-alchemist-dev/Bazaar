using System.Reflection;
using Bazaar.Server.Security.Readers;

namespace Bazaar.Server.Tests.Security.Readers;

public class HandoverStateTests
{
    private static readonly Guid _handover = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");
    private static readonly OwnerPrincipal _owner = new("owner-1");

    private static HandoverResult Valid() =>
        ReaderTestData.Validator().Validate("test.sample", ReaderTestData.Parse(ReaderTestData.MinimalJson));

    private static HandoverResult Invalid() =>
        ReaderTestData.Validator().Validate("test.sample", ReaderTestData.Parse("""{"subject":"Hello","priority":"nope","score":1}"""));

    [Fact]
    public void Accepted_validation_gives_an_accepted_state_with_fields()
    {
        var state = HandoverState.Decide(_handover, Valid());

        Assert.Equal(HandoverStatus.Accepted, state.Status);
        Assert.NotNull(state.Accepted);
        Assert.Null(state.Quarantine);
    }

    [Fact]
    public void Refused_validation_is_quarantined_with_its_reason()
    {
        var state = HandoverState.Decide(_handover, Invalid());

        Assert.Equal(HandoverStatus.Quarantined, state.Status);
        Assert.Null(state.Accepted);
        Assert.NotNull(state.Quarantine);
        Assert.Equal(_handover, state.Quarantine.HandoverId);
        Assert.Equal(HandoverRejectCode.BadEnumValue, state.Quarantine.Code);
        Assert.False(string.IsNullOrWhiteSpace(state.Quarantine.Detail));
    }

    [Fact]
    public void Owner_release_clears_the_hold_but_exposes_no_fields()
    {
        var quarantined = HandoverState.Decide(_handover, Invalid());

        var released = quarantined.Release(_owner);

        Assert.Equal(HandoverStatus.Released, released.Status);
        Assert.Null(released.Accepted);
        Assert.Equal(quarantined.Quarantine, released.Quarantine);
        Assert.Equal(HandoverStatus.Quarantined, quarantined.Status);
    }

    [Fact]
    public void Accepted_hand_over_cannot_be_released()
    {
        var state = HandoverState.Decide(_handover, Valid());

        Assert.Throws<InvalidOperationException>(() => state.Release(_owner));
    }

    [Fact]
    public void Released_hand_over_cannot_be_released_again()
    {
        var released = HandoverState.Decide(_handover, Invalid()).Release(_owner);

        Assert.Throws<InvalidOperationException>(() => released.Release(_owner));
    }

    [Fact]
    public void Release_requires_a_non_null_owner() =>
        Assert.Throws<ArgumentNullException>(() => HandoverState.Decide(_handover, Invalid()).Release(null!));

    [Fact]
    public void Owner_principal_needs_an_owner_id()
    {
        Assert.Throws<ArgumentException>(() => new OwnerPrincipal(" "));
        Assert.Equal("owner-1", _owner.OwnerId);
    }

    [Fact]
    public void Only_an_owner_principal_can_release()
    {
        // Readers and actors hold session ids and roles, never an OwnerPrincipal. Every release entry point must take one,
        // and OwnerPrincipal must not be buildable from anything but an owner id.
        var releaseMethods = typeof(HandoverState)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name.Contains("Release", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(releaseMethods);
        var parameters = releaseMethods[0].GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(OwnerPrincipal), parameters[0].ParameterType);

        // The record's compiler-generated copy constructor takes an OwnerPrincipal, not a session id.
        var constructors = typeof(OwnerPrincipal).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.DoesNotContain(constructors, ctor => ctor.GetParameters().Any(p => p.ParameterType == typeof(Guid)));
        Assert.Contains(constructors, ctor => ctor.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(string)]));
    }

    [Fact]
    public void No_conversion_turns_a_session_id_into_an_owner()
    {
        // Guid and string session ids have no conversion to OwnerPrincipal.
        var conversions = typeof(OwnerPrincipal).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .ToList();

        Assert.DoesNotContain(conversions, m => m.GetParameters()[0].ParameterType == typeof(Guid));
        Assert.DoesNotContain(conversions, m => m.GetParameters()[0].ParameterType == typeof(string));
    }
}
