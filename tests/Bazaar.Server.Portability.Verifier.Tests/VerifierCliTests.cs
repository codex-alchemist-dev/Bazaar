using System.Text.Json;

namespace Bazaar.Server.Portability.Verifier.Tests;

/// <summary>Exit codes (0 verified, 1 failed, 2 usage) and the report on stdout.</summary>
public class VerifierCliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = VerifierCli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string Pem(byte[] spki) =>
        "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(spki, Base64FormattingOptions.InsertLineBreaks) + "\n-----END PUBLIC KEY-----\n";

    [Fact]
    public void Untouched_bundle_exits_0_with_a_json_report()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        var auditKey = Path.Combine(Path.GetTempPath(), Path.GetFileName(f.Directory) + ".audit.pem");
        File.WriteAllText(auditKey, Pem(f.AuditPublicKey));
        try
        {
            var (code, output, err) = Run(f.Directory, "--audit-key", auditKey);

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, err);
            using var doc = JsonDocument.Parse(output);
            Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        }
        finally
        {
            File.Delete(auditKey);
        }
    }

    [Fact]
    public void Tampered_bundle_exits_1()
    {
        using var f = new BundleFixture();
        f.WriteUntouched();
        File.WriteAllText(f.PathOf("config/settings.json"), "{}");
        var auditKey = Path.Combine(Path.GetTempPath(), Path.GetFileName(f.Directory) + ".audit.der");
        File.WriteAllBytes(auditKey, f.AuditPublicKey);
        try
        {
            var (code, output, _) = Run(f.Directory, "--audit-key", auditKey);

            Assert.Equal(1, code);
            using var doc = JsonDocument.Parse(output);
            Assert.False(doc.RootElement.GetProperty("verified").GetBoolean());
        }
        finally
        {
            File.Delete(auditKey);
        }
    }

    [Fact]
    public void Output_option_writes_the_report_to_a_file()
    {
        using var f = new BundleFixture();
        f.Write([new BundleFile("config/settings.json", "{}"u8.ToArray())]);
        var report = Path.Combine(Path.GetTempPath(), Path.GetFileName(f.Directory) + ".report.json");
        try
        {
            var (code, output, _) = Run(f.Directory, "--output", report);

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, output);
            Assert.StartsWith("{", File.ReadAllText(report), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Theory]
    [InlineData]
    [InlineData("--bogus")]
    [InlineData("a", "b")]
    [InlineData("a", "--signing-key")]
    public void Bad_arguments_exit_2(params string[] args)
    {
        var (code, output, err) = Run(args);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("usage:", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_directory_or_key_file_exits_2()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bazaar-verify-does-not-exist-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(2, Run(missing).Code);

        using var f = new BundleFixture();
        f.Write([new BundleFile("config/settings.json", "{}"u8.ToArray())]);
        Assert.Equal(2, Run(f.Directory, "--signing-key", missing).Code);
    }
}
