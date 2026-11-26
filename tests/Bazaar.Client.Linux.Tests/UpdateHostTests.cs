using Bazaar.Client.Linux.Updates;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Linux.Tests;

public class UpdateHostTests
{
    private static UpdateHost NewHost() =>
        UpdateHost.Create(_ => null, "/home/nobody", _ => { }, TimeProvider.System, a => a());

    [Fact]
    public void Without_a_welcome_the_host_fails_closed()
    {
        using var host = NewHost();

        Assert.Null(host.Compatibility.Latest);
        Assert.NotNull(host.Compatibility.Refusal());
        Assert.Equal(string.Empty, host.Compatibility.ServerProtocol);
    }

    [Fact]
    public void A_welcome_fed_to_the_callback_reaches_the_compatibility_facts()
    {
        using var host = NewHost();

        host.OnWelcome(new Welcome(1, 0, "json", Guid.Empty, DateTimeOffset.UnixEpoch, 20, 60, new WireLimits(1024, 8, 50), [], 0, null, null, "7", false));

        Assert.NotNull(host.Compatibility.Latest);
        Assert.Equal("7", host.Compatibility.ServerSchemaVersion);
        Assert.Null(host.Compatibility.Refusal());
    }

    [Fact]
    public void A_welcome_demanding_an_upgrade_is_still_recorded_and_refuses()
    {
        using var host = NewHost();

        host.OnWelcome(new Welcome(1, 0, "json", Guid.Empty, DateTimeOffset.UnixEpoch, 20, 60, new WireLimits(1024, 8, 50), [], 0, true, null, "7", false));

        Assert.NotNull(host.Compatibility.Refusal());
    }

    [Fact]
    public void The_update_check_command_is_registered_for_the_head()
    {
        using var host = NewHost();

        var command = Assert.Single(host.Commands.Commands);
        Assert.Equal("update.check", command.Id);
        Assert.Same(UpdateCheckCommand.Command, command);
        Assert.Equal(["update.check", "update.restart"], host.Commands.Commands.Select(c => c.Id));
        Assert.Same(UpdateCheckCommand.Command, host.Commands.FindById("update.check"));
    }

    [Fact]
    public void Welcome_callback_rejects_null()
    {
        using var host = NewHost();

        Assert.Throws<ArgumentNullException>(() => host.OnWelcome(null!));
    }
}
