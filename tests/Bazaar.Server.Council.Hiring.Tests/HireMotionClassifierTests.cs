using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;

namespace Bazaar.Server.Council.Hiring.Tests;

public class HireMotionClassifierTests
{
    [Fact]
    public void Default_makes_every_membership_motion_a_bigger_change()
    {
        var classifier = new HireMotionClassifier();

        foreach (var kind in Enum.GetValues<MembershipMotionKind>())
        {
            Assert.Equal(MotionClass.BiggerChange, classifier.Classify(kind));
        }
    }

    [Fact]
    public void Default_list_names_every_membership_kind_once()
    {
        Assert.Equal(
            ["hire", "promote", "retire", "role_change", "constitution_change"],
            HireMotionClassifier.DefaultClasses);
    }

    [Fact]
    public void Hire_is_a_bigger_change_when_the_configured_list_names_it()
    {
        var classifier = new HireMotionClassifier(["hire"]);

        Assert.Equal(MotionClass.BiggerChange, classifier.Classify(MembershipMotionKind.Hire));
    }

    [Fact]
    public void Hire_is_ordinary_when_the_configured_list_omits_it()
    {
        var classifier = new HireMotionClassifier(["promote", "retire"]);

        Assert.Equal(MotionClass.Ordinary, classifier.Classify(MembershipMotionKind.Hire));
        Assert.Equal(MotionClass.BiggerChange, classifier.Classify(MembershipMotionKind.Promote));
        Assert.Equal(MotionClass.Ordinary, classifier.Classify(MembershipMotionKind.RoleChange));
    }

    [Fact]
    public void Empty_list_makes_every_motion_ordinary()
    {
        var classifier = new HireMotionClassifier([]);

        Assert.Equal(MotionClass.Ordinary, classifier.Classify(MembershipMotionKind.Hire));
    }

    [Fact]
    public void Unknown_class_key_is_rejected_so_a_typo_cannot_silently_change_the_class()
    {
        var error = Assert.Throws<ArgumentException>(() => new HireMotionClassifier(["hire", "new_agent"]));

        Assert.Contains("new_agent", error.Message);
        Assert.Contains(HireMotionClassifier.SettingKey, error.Message);
    }

    [Fact]
    public void Setting_key_is_the_documented_name()
    {
        Assert.Equal("council.bigger_change_classes", HireMotionClassifier.SettingKey);
    }
}
