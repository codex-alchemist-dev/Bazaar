using System.Text.Json;
using Bazaar.Server.MailOutbound;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailRuntime.Tests;

public sealed class MailSettingsTests
{
    private static readonly SettingActor _operator = new("op", SettingRole.Operator);

    private static JsonElement Json(string text) => JsonElement.Parse(text);

    [Fact]
    public void Defaults_are_the_values_the_code_already_ships()
    {
        Assert.True(MailSettings.RuntimeEnabled.Default);
        Assert.Equal(60, MailSettings.InboundIntervalSeconds.Default);
        Assert.Equal(30, MailSettings.OutboundIntervalSeconds.Default);
        Assert.Equal(50, MailSettings.OutboundResumeBatch.Default);
        Assert.Equal(10L * 1024 * 1024, MailSettings.MaxMessageBytes.Default);
        Assert.Equal(5, MailSettings.MaxAttachments.Default);
        Assert.Equal(20, MailSettings.NewRecipientsPerAgentDay.Default);
        Assert.Equal(20, MailSettings.SentPerAgentDay.Default);
        Assert.Equal(string.Empty, MailSettings.FooterWording.Default);
    }

    [Fact]
    public void Every_key_is_install_scope_operator_changed_and_prefixed_mail()
    {
        Assert.All(MailSettings.All, key =>
        {
            Assert.StartsWith("mail.", key.Name, StringComparison.Ordinal);
            Assert.Equal(SettingScope.Install, key.Scope);
            Assert.Equal(SettingRole.Operator, key.WhoMayChange);
        });
        Assert.Equal(MailSettings.All.Count, MailSettings.All.Select(k => k.Name).Distinct().Count());
    }

    [Fact]
    public void The_contribution_composes_with_the_built_in_registry()
    {
        var registry = SettingRegistry.WithContributions([new MailSettingContribution()]);

        Assert.All(MailSettings.All, key => Assert.True(registry.Contains(key)));
        Assert.Throws<ArgumentException>(() => SettingRegistry.WithContributions([new MailSettingContribution(), new MailSettingContribution()]));
    }

    [Theory]
    [InlineData("mail.runtime.inbound_interval_seconds", "14")]
    [InlineData("mail.runtime.inbound_interval_seconds", "3601")]
    [InlineData("mail.runtime.outbound_interval_seconds", "9")]
    [InlineData("mail.runtime.outbound_resume_batch", "0")]
    [InlineData("mail.runtime.outbound_resume_batch", "501")]
    [InlineData("mail.max_message_bytes", "26214401")]
    [InlineData("mail.max_message_bytes", "0")]
    [InlineData("mail.max_attachments", "-1")]
    [InlineData("mail.new_recipients_per_agent_day", "-1")]
    [InlineData("mail.sent_per_agent_day", "10001")]
    public void A_value_past_a_floor_or_ceiling_is_rejected(string name, string value)
    {
        var key = MailSettings.All.Single(k => k.Name == name);

        var result = key.Evaluate(_operator, Json(value));

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.NotNull(key.CheckValue(Json(value)));
    }

    [Theory]
    [InlineData("mail.runtime.inbound_interval_seconds", "15")]
    [InlineData("mail.runtime.inbound_interval_seconds", "3600")]
    [InlineData("mail.runtime.outbound_interval_seconds", "10")]
    [InlineData("mail.runtime.outbound_resume_batch", "500")]
    [InlineData("mail.max_message_bytes", "26214400")]
    [InlineData("mail.max_attachments", "0")]
    public void A_value_on_the_floor_or_ceiling_is_accepted(string name, string value) =>
        Assert.Equal(SettingWriteOutcome.Accepted, MailSettings.All.Single(k => k.Name == name).Evaluate(_operator, Json(value)).Outcome);

    [Fact]
    public void Raising_a_per_agent_limit_needs_owner_approval_but_tightening_does_not()
    {
        Assert.Equal(SettingWriteOutcome.PendingApproval, MailSettings.NewRecipientsPerAgentDay.Evaluate(_operator, Json("21")).Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, MailSettings.NewRecipientsPerAgentDay.Evaluate(_operator, Json("20")).Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, MailSettings.NewRecipientsPerAgentDay.Evaluate(_operator, Json("5")).Outcome);
        Assert.Equal(SettingWriteOutcome.PendingApproval, MailSettings.SentPerAgentDay.Evaluate(_operator, Json("21")).Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, MailSettings.SentPerAgentDay.Evaluate(_operator, Json("1")).Outcome);
    }

    [Fact]
    public void Footer_wording_change_needs_approval_and_clearing_it_does_not()
    {
        Assert.Equal(SettingWriteOutcome.PendingApproval, MailSettings.FooterWording.Evaluate(_operator, Json("\"custom wording\"")).Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, MailSettings.FooterWording.Evaluate(_operator, Json("\"\"")).Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, MailSettings.FooterWording.Evaluate(_operator, Json(JsonSerializer.Serialize(new string('x', 1001)))).Outcome);
    }

    [Fact]
    public void A_non_operator_cannot_change_a_mail_key()
    {
        var agent = new SettingActor("agent", SettingRole.Agent);

        Assert.Equal(SettingWriteOutcome.Rejected, MailSettings.RuntimeEnabled.Evaluate(agent, Json("false")).Outcome);
    }

    private sealed class FakeReader : IMailSettingReader
    {
        public Dictionary<string, object> Values { get; } = [];

        public bool Fail { get; set; }

        public int Reads { get; private set; }

        public Task<T> GetAsync<T>(SettingKey<T> key, CancellationToken ct)
        {
            Reads++;
            if (Fail)
            {
                throw new InvalidSettingValueException(key.Name, "bad");
            }

            return Task.FromResult(Values.TryGetValue(key.Name, out var v) ? (T)v : key.Default);
        }
    }

    [Fact]
    public async Task Backed_settings_start_at_the_fallback_and_follow_the_reader_after_a_refresh()
    {
        var reader = new FakeReader();
        var settings = new SettingBackedMailSettings(reader, new MailRuntimeOptions { InboundIntervalSeconds = 99 });
        Assert.Equal(99, settings.Current.InboundIntervalSeconds);
        Assert.Equal(OutboundDefaults.MaxAttachments, settings.MaxAttachments);

        reader.Values["mail.runtime.inbound_interval_seconds"] = 120;
        reader.Values["mail.runtime.enabled"] = false;
        reader.Values["mail.max_attachments"] = 2;
        reader.Values["mail.footer.wording"] = "  ";
        reader.Values["mail.footer.operator_contact"] = "ops contact";
        await settings.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(120, settings.Current.InboundIntervalSeconds);
        Assert.False(settings.Current.Enabled);
        Assert.Equal(2, settings.MaxAttachments);
        Assert.Null(settings.FooterWording);
        Assert.Equal("ops contact", settings.OperatorContact);
        Assert.Equal(OutboundDefaults.SentPerDay, settings.SentPerDay);
        Assert.Equal(DefaultOutboundMailSettings.DefaultBackendHardCapBytes, settings.BackendHardCapBytes);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_good_snapshot()
    {
        var reader = new FakeReader();
        var settings = new SettingBackedMailSettings(reader);
        reader.Values["mail.max_attachments"] = 3;
        await settings.RefreshAsync(TestContext.Current.CancellationToken);

        reader.Fail = true;
        await Assert.ThrowsAsync<InvalidSettingValueException>(() => settings.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, settings.MaxAttachments);
    }

    [Fact]
    public void Without_a_reader_the_runtime_keeps_the_static_options_and_default_outbound_settings()
    {
        var services = new ServiceCollection();
        services.AddBazaarMailRuntime();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<StaticMailRuntimeOptionsSource>(provider.GetRequiredService<IMailRuntimeOptionsSource>());
        Assert.Equal(50, provider.GetRequiredService<IMailRuntimeOptionsSource>().Current.OutboundResumeBatch);
        Assert.IsType<DefaultOutboundMailSettings>(provider.GetRequiredService<IOutboundMailSettings>());
        Assert.Contains(provider.GetServices<ISettingContribution>(), c => c is MailSettingContribution);
    }

    [Fact]
    public void With_a_reader_one_settings_backed_instance_serves_both_ports()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMailSettingReader>(new FakeReader());
        services.AddBazaarMailRuntime();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IMailRuntimeOptionsSource>();

        Assert.IsType<SettingBackedMailSettings>(options);
        Assert.Same(options, provider.GetRequiredService<IOutboundMailSettings>());
    }

    [Fact]
    public async Task The_loop_refreshes_the_source_before_each_pass()
    {
        var reader = new FakeReader();
        var settings = new SettingBackedMailSettings(reader);
        using var cts = new CancellationTokenSource();
        var passes = 0;

        await MailLoop.RunAsync(
            _ => TimeSpan.Zero,
            settings,
            TimeProvider.System,
            (_, _) =>
            {
                if (++passes == 2)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            _ => { },
            cts.Token);

        Assert.Equal(2, passes);
        Assert.True(reader.Reads >= 2 * MailSettings.All.Count - 2);
    }

    [Fact]
    public async Task A_refresh_failure_is_reported_and_the_loop_goes_on()
    {
        var reader = new FakeReader { Fail = true };
        var settings = new SettingBackedMailSettings(reader);
        using var cts = new CancellationTokenSource();
        var passes = 0;
        var failures = 0;

        await MailLoop.RunAsync(
            _ => TimeSpan.Zero,
            settings,
            TimeProvider.System,
            (_, _) =>
            {
                if (++passes == 2)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            _ => failures++,
            cts.Token);

        Assert.Equal(2, passes);
        Assert.True(failures >= 2);
    }
}
