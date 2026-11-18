using System.Net;
using Bazaar.Server.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailDns.Tests;

/// <summary>The setup step (doc 45 step 11), the plan fields it reads, and the registration extension.</summary>
public sealed class MailDnsSetupTests
{
    public const string BaseEmail = "\"email\": { \"enabled\": false }";

    private static string PlanJson(string emailSection) => $$"""
        {
          "schemaVersion": 1,
          "install": { "companyName": "Example Studio", "companySlug": "example-studio" },
          "custody": { "kekProvider": "dpapi", "kekRef": "dpapi:C:\\ProgramData\\Bazaar\\kek.bin" },
          "owner": { "displayName": "Owner" },
          "backups": { "acknowledgeNoTarget": true },
          {{emailSection}},
          "company": { "source": "blank" }
        }
        """;

    private static SetupPlan LoadPlan(string emailSection)
    {
        var result = SetupPlanLoader.Load(PlanJson(emailSection));
        Assert.True(result.Plan is not null, string.Join("\n", result.Issues.Select(i => i.Path + " " + i.Message)));
        return result.Plan;
    }

    private static SetupApplyContext Context(SetupPlan plan) => new(
        SetupPlanCanonicalizer.Hash(plan),
        SetupEntryPath.File,
        "tester",
        null,
        null,
        SetupStateSnapshot.Empty,
        []);

    private static MailDnsSetupStep Step(StubDnsResolver resolver) => new(new MailDnsChecker(resolver));

    // ---- The step ----

    [Fact]
    public async Task Step_does_nothing_when_email_is_disabled()
    {
        var resolver = new StubDnsResolver();
        var plan = LoadPlan("\"email\": { \"enabled\": false, \"domain\": \"example.com\" }");

        var result = await Step(resolver).ApplyAsync(plan, Context(plan), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Message);
        Assert.Empty(resolver.TxtQueries);
    }

    [Fact]
    public async Task Step_skips_the_check_and_succeeds_when_email_is_on_without_a_domain()
    {
        var resolver = new StubDnsResolver();
        var plan = LoadPlan("\"email\": { \"enabled\": true }");

        var result = await Step(resolver).ApplyAsync(plan, Context(plan), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("email.domain", result.Message);
        Assert.Empty(resolver.TxtQueries);
    }

    [Fact]
    public async Task Step_reports_counts_and_fixes_but_never_blocks_the_apply()
    {
        var resolver = new StubDnsResolver(); // every name is NXDOMAIN: SPF, DKIM and DMARC fail.
        var plan = LoadPlan("""
            "email": { "enabled": true, "domain": "example.com", "backendKind": "generic", "dkimSelector": "s1", "dmarcReportAddress": "reports@example.net" }
            """);

        var result = await Step(resolver).ApplyAsync(plan, Context(plan), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("3 fail", result.Message);
        Assert.Contains("does not block setup", result.Message);
        Assert.Contains("rua=mailto:reports@example.net", result.Message);
    }

    [Fact]
    public async Task Step_returns_the_four_counts_as_audit_counts()
    {
        var plan = LoadPlan("""
            "email": { "enabled": true, "domain": "example.com", "backendKind": "generic", "dkimSelector": "s1" }
            """);

        var result = await Step(new StubDnsResolver()).ApplyAsync(plan, Context(plan), CancellationToken.None);

        Assert.Equal(["fail", "not-applicable", "pass", "warn"], result.AuditCounts.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(3, result.AuditCounts["fail"]);
        Assert.All(result.AuditCounts.Values, count => Assert.True(count >= 0));
        Assert.Contains($"{result.AuditCounts["fail"]} fail", result.Message);
    }

    [Fact]
    public async Task Step_has_no_audit_counts_when_disabled_or_skipped()
    {
        var disabled = LoadPlan("\"email\": { \"enabled\": false }");
        var skipped = LoadPlan("\"email\": { \"enabled\": true }");

        Assert.Empty((await Step(new StubDnsResolver()).ApplyAsync(disabled, Context(disabled), CancellationToken.None)).AuditCounts);
        Assert.Empty((await Step(new StubDnsResolver()).ApplyAsync(skipped, Context(skipped), CancellationToken.None)).AuditCounts);
    }

    [Fact]
    public async Task Step_reports_a_clean_domain_and_is_idempotent()
    {
        var resolver = new StubDnsResolver()
            .Txt("example.com", "v=spf1 ip4:192.0.2.1 -all")
            .Txt("_dmarc.example.com", "v=DMARC1; p=reject");
        var plan = LoadPlan("\"email\": { \"enabled\": true, \"domain\": \"example.com\" }");
        var step = Step(resolver);

        var first = await step.ApplyAsync(plan, Context(plan), CancellationToken.None);
        var second = await step.ApplyAsync(plan, Context(plan), CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Contains("0 fail", first.Message);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Step_passes_the_self_hosted_flag_so_ptr_runs()
    {
        var resolver = new StubDnsResolver().Address("example.com", "192.0.2.25").Ptr("192.0.2.25", "example.com");
        var plan = LoadPlan("\"email\": { \"enabled\": true, \"domain\": \"example.com\", \"selfHosted\": true }");

        var result = await Step(resolver).ApplyAsync(plan, Context(plan), CancellationToken.None);

        Assert.DoesNotContain("Ptr ", result.Message);
        Assert.Contains("not applicable", result.Message);
    }

    [Fact]
    public async Task Step_succeeds_with_a_plain_message_when_the_checker_throws()
    {
        var plan = LoadPlan("\"email\": { \"enabled\": true, \"domain\": \"example.com\" }");

        var result = await new MailDnsSetupStep(new ThrowingChecker()).ApplyAsync(plan, Context(plan), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("boom-detail", result.Message);
    }

    [Fact]
    public void Step_runs_after_the_models_step_and_before_the_scam_mail_step()
    {
        var step = Step(new StubDnsResolver());

        Assert.InRange(step.Order, 10, 11); // doc 45 step 9 (models) < this < step 12 (scam-mail)
        Assert.Equal("email.dns", step.Id);
    }

    // ---- The plan fields ----

    [Fact]
    public void Plan_loader_accepts_the_new_email_fields()
    {
        var plan = LoadPlan("""
            "email": { "enabled": true, "domain": "mail.example.org", "backendKind": "cloudflare", "dkimSelector": "sel", "dmarcReportAddress": "r@example.org", "selfHosted": false }
            """);

        Assert.True(plan.Email.Enabled);
        Assert.Equal("mail.example.org", plan.Email.Domain);
        Assert.Equal("cloudflare", plan.Email.BackendKind);
        Assert.Equal("sel", plan.Email.DkimSelector);
        Assert.Equal("r@example.org", plan.Email.DmarcReportAddress);
        Assert.False(plan.Email.SelfHosted);
    }

    [Fact]
    public void Plan_loader_accepts_null_for_the_optional_string_fields()
    {
        var plan = LoadPlan("\"email\": { \"enabled\": true, \"domain\": null, \"backendKind\": null, \"dkimSelector\": null, \"dmarcReportAddress\": null }");

        Assert.Null(plan.Email.Domain);
    }

    [Fact]
    public void Plan_loader_still_rejects_unknown_email_fields_and_wrong_types()
    {
        var unknown = SetupPlanLoader.Load(PlanJson("\"email\": { \"enabled\": true, \"smtpPassword\": \"x\" }"));
        var wrongType = SetupPlanLoader.Load(PlanJson("\"email\": { \"enabled\": true, \"selfHosted\": \"yes\" }"));
        var nullBool = SetupPlanLoader.Load(PlanJson("\"email\": { \"enabled\": true, \"selfHosted\": null }"));

        Assert.Null(unknown.Plan);
        Assert.Contains(unknown.Issues, i => i.Path == "email.smtpPassword");
        Assert.Null(wrongType.Plan);
        Assert.Null(nullBool.Plan);
    }

    [Fact]
    public void Unset_email_fields_do_not_change_the_plan_hash_and_set_fields_do()
    {
        var plain = SetupPlanLoader.Load(PlanJson(BaseEmail));
        var explicitNulls = SetupPlanLoader.Load(PlanJson("\"email\": { \"enabled\": false, \"domain\": null }"));
        var withDomain = SetupPlanLoader.Load(PlanJson("\"email\": { \"enabled\": false, \"domain\": \"example.com\" }"));

        Assert.Equal(plain.PlanHash, explicitNulls.PlanHash);
        Assert.NotEqual(plain.PlanHash, withDomain.PlanHash);
        var canonical = System.Text.Encoding.UTF8.GetString(SetupPlanCanonicalizer.ToCanonicalUtf8(plain.Plan!));
        Assert.Contains("\"email\":{\"enabled\":false}", canonical);
    }

    // ---- Registration ----

    [Fact]
    public void AddBazaarMailDns_registers_the_resolver_the_checker_and_the_step()
    {
        using var provider = new ServiceCollection().AddBazaarMailDns().BuildServiceProvider();

        Assert.IsType<DnsClientResolver>(provider.GetRequiredService<IDnsResolver>());
        Assert.IsType<MailDnsChecker>(provider.GetRequiredService<IMailDnsChecker>());
        Assert.Single(provider.GetServices<ISetupStep>().OfType<MailDnsSetupStep>());
    }

    [Fact]
    public void AddBazaarMailDns_keeps_a_resolver_registered_earlier_and_does_not_double_register_the_step()
    {
        var stub = new StubDnsResolver();
        var services = new ServiceCollection().AddSingleton<IDnsResolver>(stub);
        services.AddBazaarMailDns();
        services.AddBazaarMailDns();
        using var provider = services.BuildServiceProvider();

        Assert.Same(stub, provider.GetRequiredService<IDnsResolver>());
        Assert.Single(provider.GetServices<ISetupStep>());
    }

    private sealed class ThrowingChecker : IMailDnsChecker
    {
        public Task<MailDnsReport> CheckAsync(MailDnsRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom-detail");
    }
}
