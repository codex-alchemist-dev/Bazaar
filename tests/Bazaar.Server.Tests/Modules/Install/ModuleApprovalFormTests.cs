using Bazaar.Server.Modules.Install;

namespace Bazaar.Server.Tests.Modules.Install;

public class ModuleApprovalFormTests
{
    [Fact]
    public void The_form_is_the_same_whatever_order_and_repeats_the_permissions_come_in()
    {
        var a = ModuleApprovalForm.Build("bazaar.sandbox", "0.1.0", ["storage:local", "network:api.example"]);
        var b = ModuleApprovalForm.Build("bazaar.sandbox", "0.1.0", ["network:api.example", "storage:local", "storage:local"]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void The_form_starts_with_its_version_byte_and_carries_the_domain_tag()
    {
        var form = ModuleApprovalForm.Build("bazaar.sandbox", "0.1.0", ["storage:local"]);

        Assert.Equal(ModuleApprovalForm.FormatVersion, form[0]);
        Assert.Contains(System.Text.Encoding.UTF8.GetBytes(ModuleApprovalForm.DomainTag), form);
    }

    [Fact]
    public void A_different_version_or_permission_set_gives_different_bytes()
    {
        var baseline = ModuleApprovalForm.Build("bazaar.sandbox", "0.1.0", ["storage:local"]);

        Assert.NotEqual(baseline, ModuleApprovalForm.Build("bazaar.sandbox", "0.1.1", ["storage:local"]));
        Assert.NotEqual(baseline, ModuleApprovalForm.Build("bazaar.sandbox", "0.1.0", ["storage:local", "network:api.example"]));
    }

    [Fact]
    public void A_permission_cannot_be_read_as_a_module_id()
    {
        // Length prefixes keep field boundaries exact: moving text between the id and a permission changes the bytes.
        var a = ModuleApprovalForm.Build("ab", "1", ["c"]);
        var b = ModuleApprovalForm.Build("a", "1", ["bc"]);

        Assert.NotEqual(a, b);
    }
}
