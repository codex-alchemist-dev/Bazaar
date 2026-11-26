using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Identity.Kek.Tpm;

namespace Bazaar.Server.Identity.Kek.Tests;

/// <summary>The <c>tpm</c> KEK provider against a fake runner. One live test runs against swtpm when BAZAAR_TEST_TPM=1.</summary>
public sealed class TpmKekProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-tpm-tests-" + Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string KeyPath => KekProviderFactory.KeyPathFor("tpm", _root);

    [Fact]
    public async Task Systemd_path_seals_once_and_reopens_to_the_same_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new FakeTpm();
        var options = new TpmKekOptions(runner, "7", SystemdPresent: true);
        var aad = new byte[] { 9 };

        using var first = Create(options);
        var wrapped = await first.WrapAsync(new byte[] { 4, 5, 6 }, aad, ct);
        using var second = Create(options);

        Assert.Equal(new byte[] { 4, 5, 6 }, await second.UnwrapAsync(wrapped, aad, ct));
        Assert.Equal(TpmKekProvider.Id, second.KekId);
        Assert.Contains(runner.Calls, c => c.StartsWith("systemd-creds encrypt --with-key=tpm2 --name=bazaar-kek --tpm2-pcrs=7", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("tpm2_create", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_systemd_path_uses_tpm2_tools_and_reopens()
    {
        var runner = new FakeTpm();
        var options = new TpmKekOptions(runner, SystemdPresent: false);

        using (Create(options)) { }
        using (Create(options)) { }

        Assert.Contains(runner.Calls, c => c.StartsWith("tpm2_create ", StringComparison.Ordinal));
        Assert.Contains(runner.Calls, c => c.StartsWith("tpm2_unseal", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("systemd-creds", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_seal_writes_no_key_file()
    {
        var runner = new FakeTpm { FailTool = "systemd-creds" };

        Assert.Throws<KekCustodyException>(() => Create(new TpmKekOptions(runner, SystemdPresent: true)));
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void Unseal_failure_fails_closed_and_leaves_the_file()
    {
        using (Create(new TpmKekOptions(new FakeTpm(), SystemdPresent: true))) { }
        var failing = new TpmKekOptions(new FakeTpm { FailTool = "systemd-creds" }, SystemdPresent: true);

        Assert.Throws<KekCustodyException>(() => Create(failing));
        Assert.True(File.Exists(KeyPath));
    }

    [Fact]
    public void Unseal_of_the_wrong_length_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The tpm provider is Linux only (doc 14).");
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, [1, 2, 3]);
        var runner = new FakeTpm { UnsealOutput = new byte[16] };

        Assert.Throws<InvalidDataException>(() => Create(new TpmKekOptions(runner, SystemdPresent: true)));
    }

    [Fact]
    public void Tampered_seal_round_trip_is_refused_before_the_file_is_written()
    {
        var runner = new FakeTpm { UnsealOutput = RandomNumberGenerator.GetBytes(32) };

        Assert.Throws<KekCustodyException>(() => Create(new TpmKekOptions(runner, SystemdPresent: true)));
        Assert.False(File.Exists(KeyPath));
    }

    [Theory]
    [InlineData("IBM", TpmKekProvider.VirtualCustody)]
    [InlineData("MSFT", TpmKekProvider.VirtualCustody)]
    [InlineData("INTC", TpmKekProvider.HardwareCustody)]
    public void Custody_class_comes_from_the_manufacturer(string manufacturer, string expected)
    {
        Assert.Equal(expected, TpmKekProvider.ProbeCustodyClass(new FakeTpm { Manufacturer = manufacturer }));
    }

    [Fact]
    public void Failed_probe_is_unverified_never_hardware()
    {
        Assert.Equal(TpmKekProvider.UnverifiedCustody, TpmKekProvider.ProbeCustodyClass(new FakeTpm { FailTool = "tpm2_getcap" }));
    }

    [Fact]
    public void Arguments_are_passed_as_a_list_without_a_shell()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Uses /bin/echo.");

        var result = new ProcessTpmToolRunner().Run("/bin/echo", ["a; touch /tmp/pwned $(id)"], null, Path.GetTempPath());

        Assert.Equal("a; touch /tmp/pwned $(id)\n", Encoding.UTF8.GetString(result.StandardOutput));
    }

    [Fact]
    public void Missing_tool_is_a_custody_error()
    {
        Assert.Throws<KekCustodyException>(() => new ProcessTpmToolRunner().Run("bazaar-no-such-tool", [], null, Path.GetTempPath()));
    }

    [Fact]
    public void Factory_lists_tpm_and_refuses_it_off_linux()
    {
        Assert.Contains("tpm", KekProviderFactory.AllowedKinds);
        Assert.SkipWhen(OperatingSystem.IsLinux(), "This checks the refusal on non-Linux hosts.");

        Assert.Throws<PlatformNotSupportedException>(() => KekProviderFactory.Create("tpm", _root));
    }

    /// <summary>Runs against swtpm. Set BAZAAR_TEST_TPM=1 and TPM2TOOLS_TCTI to a started simulator (tpm2_startup -c).</summary>
    [Fact]
    public void Live_tpm2_tools_round_trip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("BAZAAR_TEST_TPM") == "1", "Set BAZAAR_TEST_TPM=1 with TPM2TOOLS_TCTI pointing at a started swtpm.");
        var options = new TpmKekOptions(SystemdPresent: false);

        using (Create(options)) { }
        using var again = Create(options);

        Assert.Equal(TpmKekProvider.Id, again.KekId);
    }

    private TpmKekProvider Create(TpmKekOptions options)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The tpm provider is Linux only (doc 14).");
        return Assert.IsType<TpmKekProvider>(KekProviderFactory.Create("tpm", _root, options));
    }

    /// <summary>Stand-in for the tools: "seals" by prefixing the key, so unseal returns what was sealed.</summary>
    private sealed class FakeTpm : ITpmToolRunner
    {
        public List<string> Calls { get; } = [];

        public string? FailTool { get; init; }

        public byte[]? UnsealOutput { get; init; }

        public string Manufacturer { get; init; } = "INTC";

        public TpmToolResult Run(string tool, IReadOnlyList<string> arguments, byte[]? standardInput, string workingDirectory)
        {
            Calls.Add(tool + " " + string.Join(' ', arguments));
            if (tool == FailTool)
                return new TpmToolResult(1, [], "tool failed");

            switch (tool)
            {
                case "systemd-creds" when arguments[0] == "encrypt":
                    return new TpmToolResult(0, [.. "SEALED:"u8, .. standardInput!], string.Empty);
                case "systemd-creds":
                    return new TpmToolResult(0, UnsealOutput ?? standardInput!.AsSpan("SEALED:".Length).ToArray(), string.Empty);
                case "tpm2_create":
                    File.WriteAllBytes(Path.Combine(workingDirectory, "seal.pub"), [1]);
                    File.WriteAllBytes(Path.Combine(workingDirectory, "seal.priv"), standardInput!);
                    return new TpmToolResult(0, [], string.Empty);
                case "tpm2_unseal":
                    return new TpmToolResult(0, UnsealOutput ?? File.ReadAllBytes(Path.Combine(workingDirectory, "seal.priv")), string.Empty);
                case "tpm2_getcap":
                    return new TpmToolResult(0, Encoding.UTF8.GetBytes($"TPM2_PT_MANUFACTURER:\n  raw: 0x0\n  value: \"{Manufacturer}\"\n"), string.Empty);
                default:
                    return new TpmToolResult(0, [], string.Empty);
            }
        }
    }
}
