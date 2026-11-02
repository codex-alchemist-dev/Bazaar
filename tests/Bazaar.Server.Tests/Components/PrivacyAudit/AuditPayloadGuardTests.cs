using System.Text.Json.Nodes;
using Bazaar.Server.Components.PrivacyAudit;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Components.PrivacyAudit;

/// <summary>The redaction guard refuses secret-like keys, at any depth and in any letter case. Registration is checked too.</summary>
public sealed class AuditPayloadGuardTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("Refresh_Token")]
    [InlineData("secret")]
    [InlineData("client_secret")]
    [InlineData("password")]
    [InlineData("recovery_code")]
    [InlineData("code")]
    [InlineData("signing_key")]
    [InlineData("body")]
    [InlineData("message_body")]
    public void Refuses_a_top_level_secret_like_key(string key)
    {
        var payload = new JsonObject { [key] = "x", ["offer_id"] = "o1" };

        var ex = Assert.Throws<ArgumentException>(() => AuditPayloadGuard.Ensure(payload));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_secret_like_key_nested_in_an_object()
    {
        var payload = new JsonObject
        {
            ["outer"] = new JsonObject { ["inner"] = new JsonObject { ["Password"] = "x" } },
        };

        var ex = Assert.Throws<ArgumentException>(() => AuditPayloadGuard.Ensure(payload));
        Assert.Contains("outer.inner.Password", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_secret_like_key_inside_an_array()
    {
        var payload = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["ok"] = 1 }, new JsonObject { ["body"] = "text" }),
        };

        Assert.Throws<ArgumentException>(() => AuditPayloadGuard.Ensure(payload));
    }

    [Fact]
    public void Accepts_plain_identifier_and_metadata_keys()
    {
        var payload = new JsonObject
        {
            ["offer_id"] = "o1",
            ["device_id"] = "d1",
            ["role"] = "member",
            ["ttl_minutes"] = 10,
            ["nested"] = new JsonObject { ["args_hash"] = "f00d" },
        };

        AuditPayloadGuard.Ensure(payload);
    }

    [Fact]
    public void Refuses_null_payload()
    {
        Assert.Throws<ArgumentNullException>(() => AuditPayloadGuard.Ensure(null!));
    }

    [Fact]
    public void Registration_adds_the_recorder_with_system_time_when_none_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLog>(new FakeAuditLog());

        services.AddBazaarPrivacyAudit();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<PrivacyAuditRecorder>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Registration_keeps_a_time_provider_the_caller_already_registered()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLog>(new FakeAuditLog());
        services.AddSingleton<TimeProvider>(time);

        services.AddBazaarPrivacyAudit();
        using var provider = services.BuildServiceProvider();

        Assert.Same(time, provider.GetRequiredService<TimeProvider>());
    }
}
