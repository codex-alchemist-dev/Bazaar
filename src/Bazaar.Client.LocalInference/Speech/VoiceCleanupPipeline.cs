using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Client.LocalInference.Speech;

/// <summary>Cleanup output paired with the raw transcript it came from. <see cref="CleanedText"/> is null when cleanup failed or a guard rejected it.</summary>
public sealed record VoiceCleanupResult(string RawText, string? CleanedText);

/// <summary>
/// Runs on-device cleanup over a raw transcript (doc 31, Voice pipeline). The raw text is always returned unchanged;
/// any backend error or guard rejection yields a null cleaned text instead of an exception.
/// </summary>
public sealed class VoiceCleanupPipeline
{
    private const int _cleanupMaxOutputTokens = 512;

    private readonly ILocalModelBackend _backend;
    private readonly Func<string, string, bool> _guard;

    /// <param name="backend">Backend with the VoiceCleanup task kind available.</param>
    /// <param name="guard">Receives the raw and cleaned text and returns true when the cleaned text may be used.</param>
    public VoiceCleanupPipeline(ILocalModelBackend backend, Func<string, string, bool> guard)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(guard);
        _backend = backend;
        _guard = guard;
    }

    /// <summary>Cleans a raw transcript. Throws <see cref="OperationCanceledException"/> only when <paramref name="ct"/> is cancelled.</summary>
    public async Task<VoiceCleanupResult> CleanAsync(string rawText, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ct.ThrowIfCancellationRequested();

        try
        {
            var request = new LocalTaskRequest(LocalTaskKind.VoiceCleanup, rawText, _cleanupMaxOutputTokens, CacheKeyFor(rawText));
            var result = await _backend.RunAsync(request, ct);
            return _guard(rawText, result.Text)
                ? new VoiceCleanupResult(rawText, result.Text)
                : new VoiceCleanupResult(rawText, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new VoiceCleanupResult(rawText, null);
        }
    }

    private static string CacheKeyFor(string rawText) =>
        "voice-cleanup:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawText)));
}
