using System.Text.Json;
using Bazaar.Server.Settings;
using Bazaar.Server.VmTransport;
using Bazaar.Vms.Sockets;

namespace Bazaar.Server.VmTransport.Tests;

public sealed class ServiceGuidSettingTests
{
    private static JsonElement Json(string text) => JsonSerializer.SerializeToElement(text);

    [Fact]
    public void Unset_uses_the_default_without_a_problem()
    {
        var options = HvSocketListenerOptions.FromSettings(null, out var problem);

        Assert.Equal(HvSocketAddress.DefaultServiceId, options.ServiceId);
        Assert.Null(problem);
        Assert.Equal(HvSocketAddress.DefaultServiceId.ToString("D"), VmTransportSettings.ServiceGuid.Default);
    }

    [Fact]
    public void Valid_override_is_used()
    {
        var custom = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var options = HvSocketListenerOptions.FromSettings(Json(custom.ToString("D")), out var problem);

        Assert.Equal(custom, options.ServiceId);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void Invalid_value_falls_back_and_is_reported(string stored)
    {
        var options = HvSocketListenerOptions.FromSettings(Json(stored), out var problem);

        Assert.Equal(HvSocketAddress.DefaultServiceId, options.ServiceId);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Empty_guid_falls_back_and_is_reported()
    {
        var options = HvSocketListenerOptions.FromSettings(Json(Guid.Empty.ToString("D")), out var problem);

        Assert.Equal(HvSocketAddress.DefaultServiceId, options.ServiceId);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Non_string_stored_value_falls_back_and_is_reported()
    {
        var options = HvSocketListenerOptions.FromSettings(JsonSerializer.SerializeToElement(5), out var problem);

        Assert.Equal(HvSocketAddress.DefaultServiceId, options.ServiceId);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Registry_accepts_the_contribution_and_rejects_bad_writes()
    {
        var registry = SettingRegistry.Combine([], [new VmTransportSettingContribution()]);

        Assert.True(registry.TryGet("vm.transport.service_guid", out var definition));
        Assert.Same(VmTransportSettings.ServiceGuid, definition);
        var actor = new SettingActor("operator", SettingRole.Operator);
        Assert.Equal(SettingWriteOutcome.Rejected, definition!.Evaluate(actor, Json(Guid.Empty.ToString())).Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, definition.Evaluate(actor, Json("11111111-2222-3333-4444-555555555555")).Outcome);
    }
}
