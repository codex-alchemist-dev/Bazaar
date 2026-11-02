using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Core.Tests.Mobile;

public class PhoneWidgetPolicyTests
{
    [Fact]
    public void Editor_terminal_and_file_tree_are_the_desktop_only_kinds()
    {
        Assert.Equal(["editor", "file-tree", "terminal"], PhoneWidgetPolicy.DesktopOnlyKinds.Order());
    }

    [Fact]
    public void A_phone_profile_never_keeps_a_desktop_only_widget()
    {
        var profile = new[] { "gc-list", "editor", "kanban", "terminal", "approval-card", "file-tree", "diff" };

        Assert.Equal(["editor", "terminal", "file-tree"], PhoneWidgetPolicy.Violations(profile));
        Assert.Equal(["gc-list", "kanban", "approval-card", "diff"], PhoneWidgetPolicy.Keep(profile));
        Assert.Empty(PhoneWidgetPolicy.Violations(PhoneWidgetPolicy.Keep(profile)));
    }
}
