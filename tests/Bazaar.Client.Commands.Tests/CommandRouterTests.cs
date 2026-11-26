using Bazaar.Client.Commands;

namespace Bazaar.Client.Commands.Tests;

public class CommandRouterTests
{
    [Fact]
    public void A_registered_handler_runs_when_its_command_is_invoked()
    {
        var router = new CommandRouter();
        var ran = 0;
        router.Register("nav.constellation", () => ran++);

        Assert.True(router.TryInvoke("nav.constellation"));
        Assert.Equal(1, ran);
        Assert.True(router.IsHandled("nav.constellation"));
    }

    [Fact]
    public void An_unhandled_command_returns_false_and_runs_nothing()
    {
        var router = new CommandRouter();

        Assert.False(router.TryInvoke("nav.unread"));
        Assert.False(router.IsHandled("nav.unread"));
    }

    [Fact]
    public void A_second_handler_for_the_same_command_is_refused()
    {
        var router = new CommandRouter();
        router.Register("nav.settings", () => { });

        Assert.Throws<InvalidOperationException>(() => router.Register("nav.settings", () => { }));
    }

    [Fact]
    public void Disposing_a_registration_removes_the_handler_and_frees_the_command()
    {
        var router = new CommandRouter();
        var first = router.Register("nav.settings", () => { });
        first.Dispose();

        Assert.False(router.TryInvoke("nav.settings"));
        var ran = false;
        router.Register("nav.settings", () => ran = true);
        first.Dispose();
        Assert.True(router.TryInvoke("nav.settings"));
        Assert.True(ran);
    }

    [Fact]
    public void Blank_ids_and_null_handlers_are_refused()
    {
        var router = new CommandRouter();

        Assert.Throws<ArgumentException>(() => router.Register(" ", () => { }));
        Assert.Throws<ArgumentNullException>(() => router.Register("a", null!));
    }
}
