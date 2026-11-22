namespace Bazaar.Client.Mobile.Voice;

/// <summary>Records microphone audio while the radial menu's centre is held (doc 29, Radial and dictation).</summary>
public interface IDictationRecorder
{
    /// <summary>Starts recording. Called when the radial opens: dictation is live at the centre.</summary>
    void Start();

    /// <summary>Finishes recording and returns what was captured, as 16 kHz mono float32 samples.</summary>
    ReadOnlyMemory<float> Finish();
}

/// <summary>Turns audio into the raw transcript, on the device (doc 29, Voice pipeline).</summary>
public interface IDictationTranscriber
{
    Task<string> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct);
}

/// <summary>
/// Cleans a raw transcript on the device. Returns null when cleanup failed or its guard rejected the result, so the raw
/// text is used instead. The cleaner must not add facts.
/// </summary>
public interface IDictationCleaner
{
    Task<string?> CleanAsync(string rawText, CancellationToken ct);
}

/// <summary>What dictation produced. The composer receives <see cref="Text"/> for review; nothing is sent.</summary>
/// <param name="RawText">The transcript as heard.</param>
/// <param name="CleanedText">The cleaned text, or null when cleanup did not run or was rejected.</param>
public sealed record DictationResult(string RawText, string? CleanedText)
{
    /// <summary>The text to place in the composer: the cleaned text when there is one, otherwise the raw transcript.</summary>
    public string Text => CleanedText ?? RawText;
}
