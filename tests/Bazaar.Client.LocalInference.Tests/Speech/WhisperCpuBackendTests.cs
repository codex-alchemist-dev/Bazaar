using Bazaar.Client.LocalInference.Speech;

namespace Bazaar.Client.LocalInference.Tests.Speech;

public class WhisperCpuBackendTests
{
    [Fact]
    public void Missing_model_file_throws_LocalModelMissingException_with_model_and_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-wp10-absent-" + Guid.NewGuid().ToString("N"), "whisper.bin");

        var error = Assert.Throws<LocalModelMissingException>(() => new WhisperCpuBackend("whisper", path));

        Assert.Equal("whisper", error.ModelId);
        Assert.Equal(path, error.ModelPath);
    }

    [Fact]
    public void Present_model_file_selects_the_cpu_tier()
    {
        var path = Path.GetTempFileName();
        try
        {
            var backend = new WhisperCpuBackend("whisper", path);

            Assert.Equal(BackendTier.Cpu, backend.Tier);
            Assert.Equal("whisper", backend.ModelId);
            Assert.Equal(path, backend.ModelPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Decoding_is_not_implemented_yet()
    {
        var path = Path.GetTempFileName();
        try
        {
            var backend = new WhisperCpuBackend("whisper", path);

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                backend.TranscribeAsync(new float[16], CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
