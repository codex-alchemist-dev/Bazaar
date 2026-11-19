using System.IO.Compression;
using Bazaar.Server.Mail.Parsing;
using Bazaar.Server.Mail.Quarantine;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Mail.Tests.Quarantine;

/// <summary>End-to-end screening of synthetic mails (doc 22 WP3 wiring). Names are synthetic; nothing here is a real operator.</summary>
public sealed class MailScreeningServiceTests
{
    private static readonly MailAuthResults _pass = new("pass", "pass", "pass");
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private static MailScreeningService Service(MailLimits? limits = null, params string[] operatorDomains) =>
        new(new InMemoryProtectedMailDomains(operatorDomains), new MailScreeningOptions { Limits = limits ?? MailLimits.Defaults });

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entryStream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static ParsedMail Mail(string from, params (string Name, byte[] Data)[] attachments) => new(
        MessageId: "id@x.test",
        FromAddress: from,
        Subject: "s",
        TextBody: "t",
        HtmlBody: null,
        Attachments: [.. attachments.Select(a => new MailAttachment(a.Name, "application/octet-stream", a.Data.Length))],
        AutoSubmitted: false,
        PartCount: 1 + attachments.Length,
        MaxDepthSeen: 1,
        AuthenticationResults: new Dictionary<string, string>());

    private static Task<QuarantineDecision> Screen(MailScreeningService service, ParsedMail mail, (string Name, byte[] Data)[] attachments, long size = 2048) =>
        service.ScreenAsync(mail, size, _pass, i => attachments[i].Data, ct: TestContext.Current.CancellationToken);

    private static async Task<QuarantineReason[]> ReasonsAsync(MailScreeningService service, string from, params (string Name, byte[] Data)[] attachments) =>
        [.. (await Screen(service, Mail(from, attachments), attachments)).Reasons];

    [Fact]
    public async Task Clean_mail_with_a_plain_archive_is_delivered()
    {
        var zip = Zip(("a.txt", "hello"u8.ToArray()));
        var decision = await Screen(Service(null, "example.org"), Mail("a@unrelated.test", ("files.zip", zip)), [("files.zip", zip)]);

        Assert.Equal(QuarantineAction.Deliver, decision.Action);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public async Task Lookalike_of_an_operator_domain_is_held()
    {
        var reasons = await ReasonsAsync(Service(null, "example.org"), "boss@exarnple.org");

        Assert.Equal([QuarantineReason.LookalikeSenderDomain], reasons);
    }

    [Fact]
    public async Task Lookalike_screen_is_off_without_protected_domains()
    {
        Assert.Empty(await ReasonsAsync(Service(), "boss@exarnple.org"));
    }

    [Fact]
    public async Task Extra_protected_domains_from_options_are_guarded()
    {
        var service = new MailScreeningService(
            new InMemoryProtectedMailDomains(),
            new MailScreeningOptions { ExtraProtectedDomains = ["mail.example.net"] });

        Assert.Equal([QuarantineReason.LookalikeSenderDomain], await ReasonsAsync(service, "a@mail.exarnple.net"));
    }

    [Fact]
    public async Task Zip_bomb_attachment_is_held_without_unpacking()
    {
        var bomb = Zip(("zeros.bin", new byte[8 * 1024 * 1024]));

        Assert.Equal([QuarantineReason.NestedArchive], await ReasonsAsync(Service(), "a@unrelated.test", ("files.zip", bomb)));
    }

    [Fact]
    public async Task Nested_archive_attachment_is_held_even_when_renamed()
    {
        var nested = Zip(("inner.zip", "x"u8.ToArray()));

        Assert.Equal([QuarantineReason.NestedArchive], await ReasonsAsync(Service(), "a@unrelated.test", ("notes.txt", nested)));
    }

    [Fact]
    public async Task Oversize_attachment_is_skipped_and_the_message_is_held_as_oversize()
    {
        var limits = new MailLimits { MaxMessageBytes = 1024 };
        var filler = new byte[2000];
        var inspected = false;
        var mail = Mail("a@unrelated.test", ("files.zip", filler));

        var decision = await Service(limits).ScreenAsync(
            mail, 100, _pass, _ => { inspected = true; return filler; }, ct: TestContext.Current.CancellationToken);

        Assert.False(inspected);
        Assert.Contains(QuarantineReason.OversizeMessage, decision.Reasons);
    }

    [Fact]
    public async Task Message_size_alone_trips_the_oversize_screen()
    {
        var decision = await Screen(Service(new MailLimits { MaxMessageBytes = 1000 }), Mail("a@unrelated.test"), [], size: 5000);

        Assert.Equal([QuarantineReason.OversizeMessage], decision.Reasons);
    }

    [Fact]
    public async Task Missing_attachment_bytes_are_not_inspected()
    {
        var mail = Mail("a@unrelated.test", ("files.zip", new byte[10]));
        var decision = await Service().ScreenAsync(mail, 2048, _pass, _ => null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(QuarantineAction.Deliver, decision.Action);
    }

    [Fact]
    public async Task Failed_authentication_is_reported()
    {
        var decision = await Service().ScreenAsync(
            Mail("a@unrelated.test"), 2048, new MailAuthResults("fail", "pass", "pass"), _ => null, ct: TestContext.Current.CancellationToken);

        Assert.Equal([QuarantineReason.AuthenticationFailed], decision.Reasons);
    }

    [Fact]
    public async Task Tightened_limits_apply_to_both_inspection_and_rules()
    {
        var limits = new MailLimits { MaxArchiveEntries = 2 };
        var zip = Zip(("a.txt", "1"u8.ToArray()), ("b.txt", "2"u8.ToArray()), ("c.txt", "3"u8.ToArray()));

        Assert.Equal([QuarantineReason.NestedArchive], await ReasonsAsync(Service(limits), "a@unrelated.test", ("files.zip", zip)));
        Assert.Empty(await ReasonsAsync(Service(), "a@unrelated.test", ("files.zip", zip)));
    }

    [Fact]
    public void Quarantine_options_take_their_limits_from_MailLimits_alone()
    {
        var options = new InboundQuarantineOptions();

        Assert.Same(MailLimits.Defaults, options.Limits);
        var names = typeof(InboundQuarantineOptions).GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["Limits", "ProtectedDomains"], names);
    }

    [Fact]
    public async Task In_memory_domains_are_normalised_merged_and_ordered()
    {
        var domains = new InMemoryProtectedMailDomains(["B.Example", "a.example", "b.example."]);

        var set = await domains.GetAsync(["  C.example ", "", "a.example"], TestContext.Current.CancellationToken);

        Assert.Equal(["a.example", "b.example", "c.example"], set);
    }

    [Fact]
    public async Task Registration_resolves_the_service_and_lets_a_host_substitute_the_port()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProtectedMailDomains>(new InMemoryProtectedMailDomains(["example.org"]));
        new MailComponent().AddServices(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<MailScreeningService>();

        Assert.Equal([QuarantineReason.LookalikeSenderDomain], await ReasonsAsync(service, "a@exarnple.org"));
    }

    [Fact]
    public async Task Postgres_reader_returns_operator_domains_plus_extras()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PostgresMailQuarantineStoreTests.CreateDbAsync(ct);
        foreach (var (domain, state) in new[] { ("mail.example.org", "active"), ("example.net", "retired") })
        {
            await using var cmd = db.DataSource.CreateCommand(
                "INSERT INTO mail_domain (id, domain, backend_kind, state) VALUES ($1, $2, 'imap_smtp', $3)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = domain });
            cmd.Parameters.Add(new NpgsqlParameter { Value = state });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var reader = new PostgresProtectedMailDomains(db.DataSource);

        Assert.Equal(["example.net", "extra.test", "mail.example.org"], await reader.GetAsync(["Extra.test"], ct));
    }

    [Fact]
    public async Task Postgres_reader_drives_the_service_end_to_end()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PostgresMailQuarantineStoreTests.CreateDbAsync(ct);
        await using (var cmd = db.DataSource.CreateCommand("INSERT INTO mail_domain (id, domain, backend_kind) VALUES ($1, 'example.org', 'imap_smtp')"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var service = new MailScreeningService(new PostgresProtectedMailDomains(db.DataSource));

        Assert.Equal([QuarantineReason.LookalikeSenderDomain], await ReasonsAsync(service, "boss@exarnple.org"));
    }
}
