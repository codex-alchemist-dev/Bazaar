using Bazaar.Client.LocalInference.Onnx;

namespace Bazaar.Client.LocalInference.Tests.Onnx;

public class OnnxCpuBackendTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "identity.onnx");

    private static LocalTaskRequest Request(string input, int maxOutputTokens = 64) =>
        new(LocalTaskKind.ChipSummary, input, maxOutputTokens, "key-1");

    [Fact]
    public async Task Real_ORT_CPU_run_returns_the_text_through_the_identity_fixture()
    {
        await using var backend = new OnnxCpuBackend(FixturePath);

        var result = await backend.RunAsync(Request("Read 15 files"), CancellationToken.None);

        Assert.Equal("Read 15 files", result.Text);
        Assert.Equal(BackendTier.Cpu, result.Tier);
        Assert.False(result.Degraded);
        Assert.True(result.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task Input_longer_than_the_first_input_is_truncated()
    {
        await using var backend = new OnnxCpuBackend(FixturePath);
        var longInput = new string('a', 100);

        var result = await backend.RunAsync(Request(longInput), CancellationToken.None);

        Assert.Equal(new string('a', 64), result.Text);
    }

    [Fact]
    public async Task Output_is_capped_at_max_output_tokens()
    {
        await using var backend = new OnnxCpuBackend(FixturePath);

        var result = await backend.RunAsync(Request("abcdefgh", maxOutputTokens: 5), CancellationToken.None);

        Assert.Equal("abcde", result.Text);
    }

    [Fact]
    public void Missing_model_file_throws()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "Fixtures", "does-not-exist.onnx");

        Assert.Throws<FileNotFoundException>(() => new OnnxCpuBackend(missing));
    }

    [Fact]
    public async Task Cancellation_before_run_throws_OperationCanceledException()
    {
        await using var backend = new OnnxCpuBackend(FixturePath);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => backend.RunAsync(Request("hello"), cts.Token));
    }
}
