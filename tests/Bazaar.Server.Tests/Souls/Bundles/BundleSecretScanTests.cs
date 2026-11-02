using System.Text;
using Bazaar.Server.Souls.Bundles;

namespace Bazaar.Server.Tests.Souls.Bundles;

public sealed class BundleSecretScanTests
{
    // Planted test values, assembled from pieces so the literal shapes do not sit whole in source.
    private const string _openAiShape = "sk-" + "abcdefghijklmnopqrstuvwx1234";
    private const string _anthropicShape = "sk-ant-" + "api03abcdefghijklmnop";
    private const string _githubShape = "ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string _githubPatShape = "github_pat_" + "11ABCDEFG0abcdefghij_xyz";
    private const string _awsShape = "AKIA" + "IOSFODNN7EXAMPLE";
    private const string _bearerShape = "Authorization: Bearer " + "9fK2xQ7wLp4Zr8Vb";
    private const string _pemHeader = "-----BEGIN RSA " + "PRIVATE KEY-----";
    private const string _passwordShape = "password=" + "Tr0ub4dor&3xyzQ";
    private const string _tokenShape = "token = " + "9fK2xQ7wLp4Zr8Vb";

    public static TheoryData<string, string> PlantedSecrets => new()
    {
        { _openAiShape, "openai-style-api-key" },
        { _anthropicShape, "anthropic-api-key" },
        { _githubShape, "github-token" },
        { _githubPatShape, "github-fine-grained-pat" },
        { _awsShape, "aws-access-key-id" },
        { _bearerShape, "bearer-token" },
        { _pemHeader, "private-key-pem" },
        { _passwordShape, "credential-assignment" },
        { _tokenShape, "credential-assignment" },
    };

    [Theory]
    [MemberData(nameof(PlantedSecrets))]
    public void Planted_secret_shapes_are_found_by_rule(string text, string expectedRule)
    {
        var findings = BundleSecretScanner.Scan("skills/x/SKILL.md", Encoding.UTF8.GetBytes(text));

        Assert.Contains(expectedRule, findings.Select(f => f.RuleId));
        Assert.All(findings, f => Assert.Equal(1, f.Line));
    }

    [Fact]
    public void Export_refuses_a_planted_key_and_names_the_file_and_rule()
    {
        var exporter = BundleFixture.Exporter();
        var request = BundleFixture.Request([BundleFixture.Skill("skills/deploy/SKILL.md", "Step one.\nUse " + _githubShape + " for pushes.\n")]);

        var ex = Assert.Throws<BundleSecretScanException>(() => exporter.Export(request));

        var finding = Assert.Single(ex.Findings, f => f.RuleId == "github-token");
        Assert.Equal("skills/deploy/SKILL.md", finding.Path);
        Assert.Equal(2, finding.Line);
        Assert.Contains("skills/deploy/SKILL.md", ex.Message);
        Assert.Contains("github-token", ex.Message);
    }

    [Theory]
    [MemberData(nameof(PlantedSecrets))]
    public void Refusal_never_echoes_the_secret_value(string secretText, string _)
    {
        var request = BundleFixture.Request([BundleFixture.Skill("skills/leak/SKILL.md", secretText)]);

        var ex = Assert.Throws<BundleSecretScanException>(() => BundleFixture.Exporter().Export(request));

        Assert.DoesNotContain(secretText, ex.Message);
        Assert.DoesNotContain(secretText.Split(['=', ' ', ':'], StringSplitOptions.RemoveEmptyEntries).Last(), ex.Message);
        Assert.Contains("skills/leak/SKILL.md", ex.Message);
    }

    [Fact]
    public void Secret_in_the_charter_refuses_the_export()
    {
        var charter = Encoding.UTF8.GetBytes("# Charter\n" + _openAiShape + "\n");
        var version = BundleFixture.SoulVersionFor(charter);
        var request = BundleFixture.Request() with { Version = version, CharterBytes = charter };

        var ex = Assert.Throws<BundleSecretScanException>(() => BundleFixture.Exporter().Export(request));

        Assert.Contains(ex.Findings, f => f.Path == "charter.md" && f.RuleId == "openai-style-api-key");
    }

    [Fact]
    public void Every_finding_is_reported_not_only_the_first()
    {
        var request = BundleFixture.Request(
        [
            BundleFixture.Skill("skills/a/SKILL.md", _githubShape),
            BundleFixture.Skill("skills/b/SKILL.md", _awsShape),
        ]);

        var ex = Assert.Throws<BundleSecretScanException>(() => BundleFixture.Exporter().Export(request));

        Assert.Contains(ex.Findings, f => f.Path == "skills/a/SKILL.md");
        Assert.Contains(ex.Findings, f => f.Path == "skills/b/SKILL.md");
    }

    [Fact]
    public void Excluded_files_are_not_scanned_because_they_are_not_exported()
    {
        var request = BundleFixture.Request([BundleFixture.Excluded("passport/entries.json", _githubShape, "passport-entries")]);

        var result = BundleFixture.Exporter().Export(request);

        Assert.DoesNotContain("passport/entries.json", result.Files.Select(f => f.Path));
    }

    [Theory]
    [InlineData("Keep a risk-assessment log for each task-manager release.")]
    [InlineData("Token count: 12. The password policy rotates quarterly.")]
    [InlineData("Use the ghost-town-archive and the akiko-handbook folders.")]
    [InlineData("Set password: changeme in the tutorial only.")]
    public void Ordinary_prose_is_not_flagged(string text)
    {
        var findings = BundleSecretScanner.Scan("skills/prose/SKILL.md", Encoding.UTF8.GetBytes(text));

        Assert.Empty(findings);
    }
}
