using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Server.DevSandbox.Tests;

public class SandboxCompanyFactoryTests
{
    private static readonly Regex _canaryFormat = new("^CANARY-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex _emailDomain = new("@([a-z0-9.-]+)", RegexOptions.CultureInvariant);
    private static readonly Regex _urlHost = new("https?://([^/\"\\s]+)", RegexOptions.CultureInvariant);

    private static readonly string[] _allowedHosts = ["api.example.test"];

    private static readonly string[] _recordNames = ["people", "agents", "gcs", "repositories", "vault-pages", "mail", "secrets"];

    private static SandboxRunSpec Spec(string runId = "run-42", ulong seed = 12345UL)
    {
        return new SandboxRunSpec(
            RunId: runId,
            ModuleId: "sample.module",
            ModuleVersion: "1.0.0",
            Engine: SandboxEngineKind.Wasm,
            Grants: new SandboxPermissionGrant[] { new("net.egress", "api.example.test") },
            EgressAllowlist: _allowedHosts,
            Seed: seed);
    }

    [Fact]
    public void Same_seed_and_run_id_give_identical_company_byte_for_byte()
    {
        var factory = new SandboxCompanyFactory();

        SandboxTestCompany first = factory.Create(Spec());
        SandboxTestCompany second = factory.Create(Spec());

        Assert.Equal(first.CompanyId, second.CompanyId);
        Assert.Equal(first.Schema, second.Schema);
        Assert.Equal(first.Role, second.Role);
        Assert.Equal(first.Canaries, second.Canaries);
        Assert.Equal(first.Records.Keys, second.Records.Keys);
        foreach (string name in _recordNames)
        {
            Assert.Equal(first.Records[name], second.Records[name]);
        }
    }

    [Fact]
    public void Different_seed_gives_different_company()
    {
        var factory = new SandboxCompanyFactory();

        SandboxTestCompany first = factory.Create(Spec(seed: 1UL));
        SandboxTestCompany second = factory.Create(Spec(seed: 2UL));

        Assert.NotEqual(first.Canaries, second.Canaries);
        Assert.NotEqual(first.Records["secrets"], second.Records["secrets"]);
        Assert.NotEqual(first.Records["people"], second.Records["people"]);
    }

    [Fact]
    public void Records_are_the_seven_named_sets_with_expected_counts()
    {
        SandboxTestCompany company = new SandboxCompanyFactory().Create(Spec());

        Assert.Equal(_recordNames.OrderBy(name => name, StringComparer.Ordinal), company.Records.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(5, Count(company.Records["people"]));
        Assert.Equal(3, Count(company.Records["agents"]));
        Assert.Equal(5, Count(company.Records["gcs"]));
        Assert.Equal(2, Count(company.Records["repositories"]));
        Assert.Equal(4, Count(company.Records["vault-pages"]));
        Assert.Equal(4, Count(company.Records["mail"]));
        Assert.Equal(3, Count(company.Records["secrets"]));
        foreach (string text in company.Records.Values)
        {
            using JsonDocument document = JsonDocument.Parse(text);
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Creates_six_canaries_in_the_documented_format()
    {
        SandboxTestCompany company = new SandboxCompanyFactory().Create(Spec());

        Assert.Equal(6, company.Canaries.Count);
        Assert.Equal(6, company.Canaries.Distinct().Count());
        foreach (string canary in company.Canaries)
        {
            Assert.Matches(_canaryFormat, canary);
        }
    }

    [Fact]
    public void Canaries_are_planted_in_secrets_mail_and_vault_pages()
    {
        SandboxTestCompany company = new SandboxCompanyFactory().Create(Spec());

        Assert.Contains(company.Canaries[0], company.Records["secrets"]);
        Assert.Contains(company.Canaries[1], company.Records["secrets"]);
        Assert.Contains(company.Canaries[2], company.Records["mail"]);
        Assert.Contains(company.Canaries[3], company.Records["mail"]);
        Assert.Contains(company.Canaries[4], company.Records["vault-pages"]);
        Assert.Contains(company.Canaries[5], company.Records["vault-pages"]);
        foreach (string canary in company.Canaries)
        {
            Assert.True(company.Records.Values.Any(text => text.Contains(canary, StringComparison.Ordinal)), $"canary not planted: {canary}");
        }
    }

    [Fact]
    public void Only_reserved_test_domains_appear()
    {
        SandboxTestCompany company = new SandboxCompanyFactory().Create(Spec());

        foreach (string text in company.Records.Values)
        {
            foreach (Match match in _emailDomain.Matches(text))
            {
                Assert.True(
                    match.Groups[1].Value == "example.test",
                    $"unexpected email domain: {match.Groups[1].Value}");
            }

            foreach (Match match in _urlHost.Matches(text))
            {
                Assert.EndsWith(".example.test", match.Groups[1].Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Ids_and_schema_and_role_follow_the_run_id()
    {
        SandboxTestCompany company = new SandboxCompanyFactory().Create(Spec("run-42"));

        Assert.Equal("sandbox-run-42", company.CompanyId);
        Assert.Equal("sandbox_run_42", company.Schema);
        Assert.Equal("sandbox_run_42", company.Role);
        Assert.True(ProductionIsolationGuard.IsSandboxSchema(company.Schema));
    }

    [Fact]
    public void Create_refuses_a_run_id_that_cannot_name_a_sandbox_schema()
    {
        var factory = new SandboxCompanyFactory();

        Assert.Throws<ArgumentException>(() => factory.Create(Spec("Run_BAD")));
        Assert.Throws<ArgumentException>(() => factory.Create(Spec(string.Empty)));
    }

    [Fact]
    public void Create_refuses_a_null_spec()
    {
        Assert.Throws<ArgumentNullException>(() => new SandboxCompanyFactory().Create(null!));
    }

    [Fact]
    public void Output_does_not_depend_on_the_clock_or_random_guids()
    {
        string source = ReadFactorySource();

        Assert.DoesNotContain("Guid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TickCount", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Random(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RandomNumberGenerator", source, StringComparison.Ordinal);

        var factory = new SandboxCompanyFactory();
        SandboxTestCompany before = factory.Create(Spec());
        Thread.Sleep(20);
        SandboxTestCompany after = factory.Create(Spec());
        Assert.Equal(before.Records, after.Records);
        Assert.Equal(before.Canaries, after.Canaries);
    }

    private static int Count(string jsonArray)
    {
        using JsonDocument document = JsonDocument.Parse(jsonArray);
        return document.RootElement.GetArrayLength();
    }

    private static string ReadFactorySource([CallerFilePath] string callerPath = "")
    {
        string testDirectory = Path.GetDirectoryName(callerPath)!;
        string path = Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "src", "Bazaar.Server.DevSandbox", "SandboxCompanyFactory.cs"));
        return File.ReadAllText(path);
    }
}
