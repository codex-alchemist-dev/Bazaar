using Bazaar.Server.Modules.Trust;

namespace Bazaar.Server.Tests.Modules.Trust;

public class PermissionApprovalTests
{
    private static readonly string[] AddedForWidening = ["fs:write:/data", "secret:token"];
    private static readonly string[] AddedProcess = ["process:spawn"];

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
    public void Declared_superset_lists_the_added_permissions_sorted()
    {
        var result = PermissionApproval.Evaluate(
            Set("gc.post", "secret:token", "fs:write:/data", "network:api.example"),
            Set("gc.post", "network:api.example"));

        Assert.False(result.IsApproved);
        Assert.Equal(AddedForWidening, result.Unapproved);
    }

    [Fact]
    public void Widening_is_never_silently_approved_when_the_approval_is_carried_forward()
    {
        var approvedForPrevious = Set("gc.post");
        var widenedVersion = Set("gc.post", "process:spawn");

        var result = PermissionApproval.Evaluate(widenedVersion, approvedForPrevious);

        Assert.False(result.IsApproved);
        Assert.Equal(AddedProcess, result.Unapproved);
    }

    [Fact]
    public void Empty_declared_set_is_approved_with_any_approval()
    {
        Assert.True(PermissionApproval.Evaluate(Set(), Set()).IsApproved);
        Assert.True(PermissionApproval.Evaluate(Set(), Set("gc.post")).IsApproved);
    }
}
