using Bazaar.Install.Trust;

namespace Bazaar.Server.Tests.Modules.Trust;

public class PermissionApprovalTests
{
    private static readonly string[] _addedForWidening = ["fs:write:/data", "secret:token"];
    private static readonly string[] _addedProcess = ["process:spawn"];

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.Ordinal);

    [Fact]
    public void Declared_equal_to_approved_is_approved()
    {
        var result = PermissionApproval.Evaluate(Set("gc.post", "network:api.example"), Set("gc.post", "network:api.example"));

        Assert.True(result.IsApproved);
        Assert.Empty(result.Unapproved);
    }

    [Fact]
    public void Declared_subset_of_approved_is_approved()
    {
        var result = PermissionApproval.Evaluate(Set("gc.post"), Set("gc.post", "network:api.example"));

        Assert.True(result.IsApproved);
        Assert.Empty(result.Unapproved);
    }

    [Fact]
    public void Narrowing_from_the_approved_version_needs_no_new_approval()
    {
        var previouslyApproved = Set("gc.post", "network:api.example", "fs:read:notes");
        var narrowed = Set("gc.post");

        Assert.True(PermissionApproval.Evaluate(narrowed, previouslyApproved).IsApproved);
    }

    [Fact]
    public void A_permission_below_an_approved_prefix_grant_is_a_narrowing()
    {
        var approved = Set("secret:weather/*", "fs:data/weather/*");

        Assert.True(PermissionApproval.Evaluate(Set("secret:weather/api-key", "fs:data/weather/cache/*"), approved).IsApproved);
    }

    [Theory]
    [InlineData("secret:weather")]
    [InlineData("secret:weather-admin/key")]
    [InlineData("secret:*")]
    [InlineData("secret:weather/../owner/key")]
    [InlineData("fs:weather/api-key")]
    [InlineData("secret:/*")]
    public void Nothing_outside_an_approved_prefix_grant_rides_on_it(string declared)
    {
        var result = PermissionApproval.Evaluate(Set(declared), Set("secret:weather/*"));

        Assert.False(result.IsApproved);
        Assert.Equal([declared], result.Unapproved);
    }

    [Fact]
    public void An_exact_approval_never_covers_a_prefix_grant()
    {
        Assert.False(PermissionApproval.Evaluate(Set("secret:weather/*"), Set("secret:weather/api-key")).IsApproved);
    }

    [Fact]
    public void Declared_superset_lists_the_added_permissions_sorted()
    {
        var result = PermissionApproval.Evaluate(
            Set("gc.post", "secret:token", "fs:write:/data", "network:api.example"),
            Set("gc.post", "network:api.example"));

        Assert.False(result.IsApproved);
        Assert.Equal(_addedForWidening, result.Unapproved);
    }

    [Fact]
    public void Widening_is_never_silently_approved_when_the_approval_is_carried_forward()
    {
        var approvedForPrevious = Set("gc.post");
        var widenedVersion = Set("gc.post", "process:spawn");

        var result = PermissionApproval.Evaluate(widenedVersion, approvedForPrevious);

        Assert.False(result.IsApproved);
        Assert.Equal(_addedProcess, result.Unapproved);
    }

    [Fact]
    public void Empty_declared_set_is_approved_with_any_approval()
    {
        Assert.True(PermissionApproval.Evaluate(Set(), Set()).IsApproved);
        Assert.True(PermissionApproval.Evaluate(Set(), Set("gc.post")).IsApproved);
    }
}
