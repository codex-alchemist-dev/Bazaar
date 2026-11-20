using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Generic;
using Bazaar.Server.MailInbound;
using Bazaar.Server.MailOutbound;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.MailRuntime.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void Defaults_are_the_shipped_values()
    {
        var o = new MailRuntimeOptions();
        Assert.True(o.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(60), o.EffectiveInboundInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), o.EffectiveOutboundInterval);
        Assert.Equal(50, o.EffectiveOutboundResumeBatch);
    }

    [Theory]
    [InlineData(-5, 15)]
    [InlineData(0, 15)]
    [InlineData(1, 15)]
    [InlineData(90, 90)]
    [InlineData(999999, 3600)]
    public void Inbound_interval_never_goes_below_the_floor_or_above_the_ceiling(int setting, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), new MailRuntimeOptions { InboundIntervalSeconds = setting }.EffectiveInboundInterval);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(45, 45)]
    [InlineData(100000, 3600)]
    public void Outbound_interval_never_goes_below_the_floor_or_above_the_ceiling(int setting, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), new MailRuntimeOptions { OutboundIntervalSeconds = setting }.EffectiveOutboundInterval);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(80, 80)]
    [InlineData(9000, 500)]
    public void Resume_batch_is_clamped(int setting, int expected) =>
        Assert.Equal(expected, new MailRuntimeOptions { OutboundResumeBatch = setting }.EffectiveOutboundResumeBatch);
}

public sealed class FailClosedTests
{
    [Fact]
    public async Task Approval_gateway_refuses_requests_and_never_approves_or_consumes()
    {
        var gateway = new FailClosedMailApprovalGateway();
        await Assert.ThrowsAsync<MailPortNotConfiguredException>(() => gateway.RequestAsync(null!, CancellationToken.None));
        Assert.Equal(MailApprovalStatus.Pending, await gateway.GetStatusAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.False(await gateway.ConsumeAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Sealers_and_protector_refuse_so_no_clear_text_is_stored()
    {
        await Assert.ThrowsAsync<MailPortNotConfiguredException>(async () => await new FailClosedMailBodySealer().SealAsync(Guid.NewGuid(), new byte[] { 1 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<MailPortNotConfiguredException>(async () => await new FailClosedMailBodySealer().OpenAsync(Guid.NewGuid(), new SealedBody(Guid.Empty, [], []), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<MailPortNotConfiguredException>(async () => await new FailClosedMailBodyProtector().ProtectAsync("agent", new byte[] { 1 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<MailPortNotConfiguredException>(async () => await new FailClosedMailBodyProtector().UnprotectAsync(new ProtectedBody(Guid.Empty, [], []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reader_taint_and_triage_defaults_refuse()
    {
        await Assert.ThrowsAsync<MailPortNotConfiguredException>(() =>
            new FailClosedMailReaderSessions().StartAsync(new ReaderSessionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
        await Assert.ThrowsAsync<MailPortNotConfiguredException>(() =>
            new FailClosedReaderSessionTaint().MarkHardAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<MailPortNotConfiguredException>(() =>
            new FailClosedMailTriageReader().TriageAsync(new ReaderSessionHandle(Guid.NewGuid()), new MailReaderInput(Guid.NewGuid(), null, null, null, null, []), CancellationToken.None));
    }

    [Fact]
    public async Task Directory_is_empty_and_no_recipient_is_approved()
    {
        Assert.Empty(await new FailClosedInboundMailboxDirectory().ListActiveAsync(CancellationToken.None));
        Assert.False(await new FailClosedApprovedRecipients().IsApprovedAsync("agent", "someone@recipient.example", CancellationToken.None));
    }

    [Fact]
    public void Defaults_step_aside_for_a_host_registration_and_readiness_names_the_rest()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMailBodySealer, OkSealer>();
        services.AddSingleton<IMailApprovalGateway, OkGateway>();
        services.AddSingleton(new Npgsql.NpgsqlDataSourceBuilder("Host=localhost").Build());
        services.AddSingleton<IMailGcAppender>(new RecordingAppender());
        services.AddBazaarMailRuntime();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<OkSealer>(sp.GetRequiredService<IMailBodySealer>());
        Assert.Equal(
            ["IMailReaderSessions", "IReaderSessionTaint", "IMailTriageReader"],
            MailRuntimeReadiness.MissingInbound(sp));
        Assert.Equal(["IMailBodyProtector"], MailRuntimeReadiness.MissingOutbound(sp));
    }

    [Fact]
    public void Registering_twice_adds_each_hosted_service_once()
    {
        var services = new ServiceCollection();
        services.AddBazaarMailRuntime();
        services.AddBazaarMailRuntime();
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IHostedService)));
    }
}

public sealed class OutboundMailNoticesTests
{
    private static readonly Guid _gc = Guid.NewGuid();
    private static readonly DateTimeOffset _at = new(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);

    private static OutboundRefusalNotice Notice(Guid id) => new("agent-1", id, "approval_refused:not_permitted", "mail.example", 3, _at);

    [Fact]
    public async Task The_notice_is_a_system_event_with_only_the_allowed_fields()
    {
        var appender = new RecordingAppender();
        var id = Guid.NewGuid();
        var notices = new OutboundMailNotices(appender, new StaticMailRuntimeOptionsSource(new MailRuntimeOptions { NoticesGcId = _gc }));

        await notices.PostApprovalRefusedAsync(Notice(id), CancellationToken.None);
        await notices.PostApprovalRefusedAsync(Notice(id), CancellationToken.None);

        Assert.Equal(2, appender.Requests.Count);
        var request = appender.Requests[0];
        Assert.Equal(_gc, request.GcId);
        Assert.Equal(GcEventKinds.System, request.Kind);
        Assert.Equal(AuthorKind.System, request.AuthorKind);
        Assert.Equal(SourceTrust.System, request.SourceTrust);
        Assert.Equal(appender.Requests[0].ClientMsgId, appender.Requests[1].ClientMsgId);
        using var doc = JsonDocument.Parse(request.PayloadJson!);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["agent_id", "at", "message_id", "notice", "reason_code", "recipient_count", "sender_domain"], names);
        Assert.Equal(OutboundMailNotices.ApprovalRefusedKind, doc.RootElement.GetProperty("notice").GetString());
        Assert.Equal("mail.example", doc.RootElement.GetProperty("sender_domain").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("recipient_count").GetInt32());
        Assert.Equal(id, doc.RootElement.GetProperty("message_id").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unset_target_gc_skips_the_notice_and_counts_it(bool empty)
    {
        var appender = new RecordingAppender();
        var options = new MailRuntimeOptions { NoticesGcId = empty ? Guid.Empty : null };
        var notices = new OutboundMailNotices(appender, new StaticMailRuntimeOptionsSource(options), NullLogger<OutboundMailNotices>.Instance);

        await notices.PostApprovalRefusedAsync(Notice(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(appender.Requests);
        Assert.Equal(1, notices.SkippedCount);
    }

    [Fact]
    public void The_runtime_registers_the_notice_adapter_and_a_host_port_wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMailGcAppender>(new RecordingAppender());
        services.AddBazaarMailRuntime();
        Assert.Contains(services, d => d.ServiceType == typeof(IOutboundMailNotices));

        var own = new NoOutboundMailNotices();
        var hosted = new ServiceCollection();
        hosted.AddSingleton<IOutboundMailNotices>(own);
        hosted.AddBazaarMailRuntime();
        Assert.Single(hosted, d => d.ServiceType == typeof(IOutboundMailNotices));
    }
}

public sealed class InboxGcPosterTests
{
    private static readonly Guid _gc = Guid.NewGuid();

    [Fact]
    public async Task External_mail_is_posted_by_the_bridge_as_hard_tainted_external_with_metadata_only()
    {
        var appender = new RecordingAppender();
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        var eventId = await new InboxGcPoster(appender).PostExternalMailAsync(
            new ExternalMailEntry(_gc, Guid.NewGuid(), id, "sender.example", at, ["code_one"]), CancellationToken.None);

        var request = Assert.Single(appender.Requests);
        Assert.NotEqual(Guid.Empty, eventId);
        Assert.Equal(GcEventKinds.ExternalMail, request.Kind);
        Assert.Equal(AuthorKind.ExternalBridge, request.AuthorKind);
        Assert.Equal(SourceTrust.External, request.SourceTrust);
        Assert.Equal(InboxGcPoster.BridgeAuthorId, request.AuthorId);
        Assert.True(request.TaintedAtWrite);
        Assert.Null(request.Message);
        Assert.Equal("mail.ext:" + id.ToString("N"), request.ClientMsgId);

        using var doc = JsonDocument.Parse(request.PayloadJson!);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["mail_message_id", "mailbox_id", "received_at", "screen_codes", "sender_domain"], names);
    }

    [Fact]
    public async Task A_repeat_uses_the_same_idempotency_key()
    {
        var appender = new RecordingAppender();
        var poster = new InboxGcPoster(appender);
        var entry = new ExternalMailEntry(_gc, Guid.NewGuid(), Guid.NewGuid(), "sender.example", DateTimeOffset.UnixEpoch, []);
        await poster.PostExternalMailAsync(entry, CancellationToken.None);
        await poster.PostExternalMailAsync(entry, CancellationToken.None);
        Assert.Equal(appender.Requests[0].ClientMsgId, appender.Requests[1].ClientMsgId);
    }

    [Fact]
    public async Task Quarantine_notice_carries_a_count_and_closed_codes_and_no_sender()
    {
        var appender = new RecordingAppender();
        var id = Guid.NewGuid();
        await new InboxGcPoster(appender).PostQuarantineNoticeAsync(new QuarantineNotice(_gc, id, ["spf_fail", "oversize"], 3), CancellationToken.None);

        var request = Assert.Single(appender.Requests);
        Assert.Equal(GcEventKinds.System, request.Kind);
        Assert.Equal(AuthorKind.System, request.AuthorKind);
        Assert.Equal(SourceTrust.System, request.SourceTrust);
        using var doc = JsonDocument.Parse(request.PayloadJson!);
        Assert.Equal(3, doc.RootElement.GetProperty("held_count").GetInt32());
        Assert.Equal(["spf_fail", "oversize"], doc.RootElement.GetProperty("reason_codes").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(doc.RootElement.TryGetProperty("sender_domain", out _));
    }

    [Fact]
    public async Task Triage_handover_is_posted_with_the_verdict_and_codes_only()
    {
        var appender = new RecordingAppender();
        var handover = new MailTriageHandover(Guid.NewGuid(), MailTriageVerdict.NeedsAttention, [MailTriageReasonCodes.OwnerRequest]);
        await new InboxGcPoster(appender).PostTriageHandoverAsync(_gc, handover, CancellationToken.None);

        var request = Assert.Single(appender.Requests);
        Assert.Equal(GcEventKinds.Handover, request.Kind);
        using var doc = JsonDocument.Parse(request.PayloadJson!);
        Assert.Equal("mail_triage", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("needs_attention", doc.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task An_invalid_handover_is_refused_and_nothing_is_posted()
    {
        var appender = new RecordingAppender();
        var bad = new MailTriageHandover(Guid.NewGuid(), MailTriageVerdict.Routine, ["free text from the message"]);
        await Assert.ThrowsAsync<ArgumentException>(() => new InboxGcPoster(appender).PostTriageHandoverAsync(_gc, bad, CancellationToken.None));
        Assert.Empty(appender.Requests);
    }
}

public sealed class BackendCatalogTests
{
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    private static string Json(object value) => JsonSerializer.Serialize(value, _web);

    [Fact]
    public async Task Builds_a_back_end_per_active_domain_from_stored_configuration_and_skips_the_rest()
    {
        var store = new FakeSecretStore(new()
        {
            ["mail.cfg.one"] = Json(new { imapHost = "imap.provider.example", imapPort = 993, imapSecurity = "SslOnConnect", smtpHost = "smtp.provider.example", smtpPort = 587, smtpSecurity = "StartTls" }),
            ["mail.cfg.two"] = Json(new { apiBase = "https://api.sender.example/v1" }),
        });
        var domains = new ListDomainSource(
            new MailDomainConfig("First.Example", "imap_smtp", "mail.cfg.one"),
            new MailDomainConfig("second.example", "cloudflare", "mail.cfg.two"),
            new MailDomainConfig("third.example", "bazaar_client", null));
        using var catalog = new MailBackendCatalog(domains, store, new SecretStoreMailCredentialSource(store));

        Assert.Null(catalog.Resolve("first.example"));
        var result = await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, result.Built);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(result.ErrorKinds);
        Assert.IsType<ImapSmtpBackend>(catalog.Resolve("FIRST.example"));
        Assert.NotNull(catalog.Resolve("second.example"));
        Assert.Null(catalog.Resolve("third.example"));
        Assert.Null(catalog.Resolve("unknown.example"));
        Assert.Null(catalog.Resolve(""));
    }

    [Fact]
    public async Task A_broken_domain_is_reported_by_error_kind_and_does_not_stop_the_others()
    {
        var store = new FakeSecretStore(new()
        {
            ["mail.cfg.bad"] = Json(new { apiBase = "http://plain.example/" }),
            ["mail.cfg.ok"] = Json(new { apiBase = "https://api.sender.example" }),
        });
        var domains = new ListDomainSource(
            new MailDomainConfig("bad.example", "cloudflare", "mail.cfg.bad"),
            new MailDomainConfig("missing.example", "imap_smtp", "mail.cfg.absent"),
            new MailDomainConfig("noref.example", "imap_smtp", null),
            new MailDomainConfig("ok.example", "cloudflare", "mail.cfg.ok"));
        using var catalog = new MailBackendCatalog(domains, store, new SecretStoreMailCredentialSource(store));

        var result = await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, result.Built);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(3, result.ErrorKinds.Count);
        Assert.All(result.ErrorKinds, k => Assert.DoesNotContain("example", k, StringComparison.Ordinal));
        Assert.NotNull(catalog.Resolve("ok.example"));
        Assert.Null(catalog.Resolve("bad.example"));
    }

    [Fact]
    public async Task Credentials_come_from_the_secret_store_under_a_name_derived_from_the_address()
    {
        var login = new MailCredential("agent-login", string.Concat("not", "-", "real"));
        var box = new MailboxRef("sender.example", "agent.one");
        var name = SecretStoreMailCredentialSource.NameFor(box);
        Assert.Equal("mail.login.agent.one.sender.example", name.Value);

        var store = new FakeSecretStore(new() { [name.Value] = Json(login) });
        var got = await new SecretStoreMailCredentialSource(store).GetAsync(box, CancellationToken.None);
        Assert.Equal(login, got);
        Assert.Equal([name.Value], store.Requested);
    }

    [Fact]
    public async Task An_empty_stored_login_is_refused()
    {
        var box = new MailboxRef("sender.example", "agent");
        var store = new FakeSecretStore(new() { [SecretStoreMailCredentialSource.NameFor(box).Value] = Json(new MailCredential("", "")) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SecretStoreMailCredentialSource(store).GetAsync(box, CancellationToken.None));
    }
}

public sealed class HostedServiceTests
{
    private static readonly MailRuntimeOptions _fast = new() { InboundIntervalSeconds = 15, OutboundIntervalSeconds = 10 };

    private static async Task DriveAsync(FakeTimeProvider time, Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(done(), "The loop did not reach the expected state.");
    }

    private static ServiceProvider InboundProvider(IInboundMailWorker worker, bool ready)
    {
        var services = new ServiceCollection();
        services.AddSingleton(worker);
        if (ready)
        {
            services.AddSingleton<IInboundMailboxDirectory, OkDirectory>();
            services.AddSingleton<IMailBodySealer, OkSealer>();
            services.AddSingleton<IMailReaderSessions, OkSessions>();
            services.AddSingleton<IReaderSessionTaint, OkTaint>();
            services.AddSingleton<IMailTriageReader, OkReader>();
        }
        else
        {
            services.AddSingleton<IInboundMailboxDirectory, FailClosedInboundMailboxDirectory>();
            services.AddSingleton<IMailBodySealer, OkSealer>();
        }

        return services.BuildServiceProvider();
    }

    private static InboundPassResult Clean() => new([new MailboxPassResult(Guid.NewGuid(), 2, 1, 1, 0, 0, [])]);

    [Fact]
    public async Task Inbound_loop_runs_passes_on_the_interval_and_stops_on_cancellation()
    {
        var worker = new ScriptedWorker(_ => Task.FromResult(Clean()));
        using var sp = InboundProvider(worker, ready: true);
        var time = new FakeTimeProvider();
        using var service = new InboundMailHostedService(sp, new StaticMailRuntimeOptionsSource(_fast), NullLogger<InboundMailHostedService>.Instance, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await DriveAsync(time, () => worker.Calls >= 3);
        await service.StopAsync(TestContext.Current.CancellationToken);

        var calls = worker.Calls;
        time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Equal(calls, worker.Calls);
    }

    [Fact]
    public async Task A_failed_inbound_pass_never_stops_the_loop()
    {
        var worker = new ScriptedWorker(n => n == 1
            ? throw new InvalidOperationException("subject line from a message")
            : Task.FromResult(Clean()));
        using var sp = InboundProvider(worker, ready: true);
        var time = new FakeTimeProvider();
        using var service = new InboundMailHostedService(sp, new StaticMailRuntimeOptionsSource(_fast), NullLogger<InboundMailHostedService>.Instance, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await DriveAsync(time, () => worker.Calls >= 3);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Inbound_loop_does_not_pull_while_a_required_port_is_fail_closed()
    {
        var worker = new ScriptedWorker(_ => Task.FromResult(Clean()));
        using var sp = InboundProvider(worker, ready: false);
        var time = new FakeTimeProvider();
        using var service = new InboundMailHostedService(sp, new StaticMailRuntimeOptionsSource(_fast), NullLogger<InboundMailHostedService>.Instance, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task Inbound_loop_does_nothing_while_disabled()
    {
        var worker = new ScriptedWorker(_ => Task.FromResult(Clean()));
        using var sp = InboundProvider(worker, ready: true);
        var time = new FakeTimeProvider();
        using var service = new InboundMailHostedService(
            sp, new StaticMailRuntimeOptionsSource(new MailRuntimeOptions { Enabled = false }), NullLogger<InboundMailHostedService>.Instance, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, worker.Calls);
    }

    private static ServiceProvider OutboundProvider(IOutboundMailPipeline pipeline, FakeCatalog catalog, bool ready)
    {
        var services = new ServiceCollection();
        services.AddSingleton(pipeline);
        services.AddSingleton<IMailBackendCatalog>(catalog);
        if (ready)
        {
            services.AddSingleton<IMailApprovalGateway, OkGateway>();
            services.AddSingleton<IMailBodyProtector, OkProtector>();
        }
        else
        {
            services.AddSingleton<IMailApprovalGateway, FailClosedMailApprovalGateway>();
            services.AddSingleton<IMailBodyProtector, OkProtector>();
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Outbound_loop_refreshes_back_ends_and_resumes_with_the_configured_batch()
    {
        var pipeline = new ScriptedPipeline((_, _) => Task.FromResult(2));
        var catalog = new FakeCatalog();
        using var sp = OutboundProvider(pipeline, catalog, ready: true);
        var time = new FakeTimeProvider();
        var options = new StaticMailRuntimeOptionsSource(new MailRuntimeOptions { OutboundResumeBatch = 7, OutboundIntervalSeconds = 10 });
        using var service = new OutboundMailResumeHostedService(sp, options, NullLogger<OutboundMailResumeHostedService>.Instance, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await DriveAsync(time, () => pipeline.Calls >= 2);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, pipeline.LastMax);
        Assert.True(catalog.Refreshes >= 2);
    }

    [Fact]
    public async Task A_failed_outbound_pass_never_stops_the_loop()
    {
        var pipeline = new ScriptedPipeline((n, _) => n == 1 ? throw new TimeoutException() : Task.FromResult(0));
        using var sp = OutboundProvider(pipeline, new FakeCatalog(), ready: true);
        var time = new FakeTimeProvider();
        using var service = new OutboundMailResumeHostedService(sp, new StaticMailRuntimeOptionsSource(_fast), NullLogger<OutboundMailResumeHostedService>.Instance, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await DriveAsync(time, () => pipeline.Calls >= 3);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Outbound_loop_does_not_resume_while_the_approval_gateway_is_fail_closed()
    {
        var pipeline = new ScriptedPipeline((_, _) => Task.FromResult(0));
        using var sp = OutboundProvider(pipeline, new FakeCatalog(), ready: false);
        var time = new FakeTimeProvider();
        using var service = new OutboundMailResumeHostedService(sp, new StaticMailRuntimeOptionsSource(_fast), NullLogger<OutboundMailResumeHostedService>.Instance, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, pipeline.Calls);
    }
}
