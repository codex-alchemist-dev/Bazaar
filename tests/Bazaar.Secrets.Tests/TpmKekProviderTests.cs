using System.Text;
using Bazaar.Secrets.Keyring;
using Bazaar.Secrets.Tpm;

namespace Bazaar.Secrets.Tests;

public sealed class TpmKekProviderTests : IDisposable
{
    private static readonly byte[] _aad = Encoding.UTF8.GetBytes("bazaar.secrets.datakey.v1|installation|kek");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-tpm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Runs against a real TPM (swtpm). Set BAZAAR_TEST_TPM=1 and TPM2TOOLS_TCTI to the simulator, after tpm2_startup -c.</summary>
    [Fact]
    public async Task LiveTpmSealsTheKekAndUnsealsItLater()
    {
        if (Environment.GetEnvironmentVariable("BAZAAR_TEST_TPM") != "1")
        {
            Assert.Skip("Set BAZAAR_TEST_TPM=1 with TPM2TOOLS_TCTI pointing at a started TPM (swtpm) to run this test.");
        }

        var runner = new ProcessTpmToolRunner();
        using var first = await TpmKekProvider.OpenAsync(runner, _dir, "tpm-test-kek", TestContext.Current.CancellationToken);
        var wrapped = await first.WrapAsync(new byte[] { 4, 5, 6 }, _aad, TestContext.Current.CancellationToken);
        using var second = await TpmKekProvider.OpenAsync(runner, _dir, "tpm-test-kek", TestContext.Current.CancellationToken);
        var unwrapped = await second.UnwrapAsync("tpm-test-kek", wrapped, _aad, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 4, 5, 6 }, unwrapped.ToArray());
        Assert.True(File.Exists(Path.Combine(_dir, "tpm-test-kek.priv")));
    }

    [Fact]
    public async Task HalfWrittenStateThrowsAndCreatesNothing()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "kek-1.pub"), new byte[] { 1 }, TestContext.Current.CancellationToken);
        var runner = new FakeTpm();

        await Assert.ThrowsAsync<KekCustodyException>(() => TpmKekProvider.OpenAsync(runner, _dir, "kek-1", TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UnsealOfTheWrongLengthThrows()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "kek-1.pub"), new byte[] { 1 }, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "kek-1.priv"), new byte[] { 2 }, TestContext.Current.CancellationToken);
        var runner = new FakeTpm { UnsealOutput = new byte[16] };

        await Assert.ThrowsAsync<KekCustodyException>(() => TpmKekProvider.OpenAsync(runner, _dir, "kek-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedPrimaryCreationWritesNoBlobs()
    {
        var runner = new FakeTpm { FailTool = "tpm2_createprimary" };

        await Assert.ThrowsAsync<KekCustodyException>(() => TpmKekProvider.OpenAsync(runner, _dir, "kek-1", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_dir, "kek-1.pub")));
        Assert.False(File.Exists(Path.Combine(_dir, "kek-1.priv")));
    }

    [Fact]
    public async Task InvalidKeyIdIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => TpmKekProvider.OpenAsync(new FakeTpm(), _dir, "../escape", TestContext.Current.CancellationToken));
    }

    /// <summary>Stand-in for tpm2-tools that records calls and writes the blobs a real create would.</summary>
    private sealed class FakeTpm : ITpmToolRunner
    {
        public List<string> Calls { get; } = [];

        public string? FailTool { get; init; }

        public byte[]? UnsealOutput { get; init; }

        public Task<TpmToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, byte[]? standardInput, string workingDirectory, CancellationToken ct)
        {
            Calls.Add(tool);
            if (tool == FailTool)
            {
                return Task.FromResult(new TpmToolResult(1, [], "primary failed"));
            }

            if (tool == "tpm2_create")
            {
                File.WriteAllBytes(Path.Combine(workingDirectory, "seal.pub"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(workingDirectory, "seal.priv"), new byte[] { 2 });
            }

            if (tool == "tpm2_unseal")
            {
                return Task.FromResult(new TpmToolResult(0, UnsealOutput ?? [], string.Empty));
            }

            return Task.FromResult(new TpmToolResult(0, [], string.Empty));
        }
    }
}
