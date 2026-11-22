namespace Bazaar.Client.Mobile.Voice;

/// <summary>
/// Joins the radial menu to the voice pipeline (doc 29). Recording follows the finger: it starts when dictation goes live at
/// the centre and stops when the finger leaves the centre or lifts. Only a release in the centre produces a transcript;
/// entering a sector, releasing in a sector or on empty space, or an interrupted gesture discards the audio. The result goes
/// to <see cref="TranscriptReady"/> for the composer to show for review. The controller never sends text anywhere and never
/// approves or denies anything: dictation is input to the composer only (doc 29, Approvals).
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly IDictationRecorder _recorder;
    private readonly IDictationTranscriber _transcriber;
    private readonly IDictationCleaner? _cleaner;
    private readonly Action<string>? _log;
    private CancellationTokenSource _work = new();
    private ReadOnlyMemory<float>? _pending;
    private bool _recording;

    public DictationController(
        IDictationRecorder recorder,
        IDictationTranscriber transcriber,
        IDictationCleaner? cleaner = null,
        Action<string>? log = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _cleaner = cleaner;
        _log = log;
    }

    /// <summary>A transcript is ready to be placed in the composer for review.</summary>
    public event EventHandler<DictationResult>? TranscriptReady;

    /// <summary>True while the recorder is running.</summary>
    public bool IsRecording => _recording;

    /// <summary>Connects to a phone shell's events. Dispose the controller to disconnect.</summary>
    public void Attach(PhoneShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        shell.DictationChanged += OnDictationChanged;
        shell.RadialReleased += OnRadialReleased;
        _detach = () =>
        {
            shell.DictationChanged -= OnDictationChanged;
            shell.RadialReleased -= OnRadialReleased;
        };
    }

    private Action? _detach;

    /// <summary>Dictation went live (true) or stopped (false), as the radial raises it.</summary>
    public void OnDictationChanged(object? sender, bool live)
    {
        if (live)
        {
            // A new gesture starts clean: audio left from an interrupted one is dropped.
            _pending = null;
            _recorder.Start();
            _recording = true;
            return;
        }

        if (_recording)
        {
            _pending = _recorder.Finish();
            _recording = false;
        }
    }

    /// <summary>The radial was released. Only a release in the centre turns the pending audio into a transcript.</summary>
    public void OnRadialReleased(object? sender, RadialRelease release)
    {
        var audio = _pending;
        _pending = null;
        if (release.Outcome != RadialOutcome.PlaceTranscript || audio is null || audio.Value.IsEmpty)
        {
            return;
        }

        _ = TranscribeAsync(audio.Value, _work.Token);
    }

    /// <summary>Process death or teardown: recording stops, pending audio is dropped and nothing is sent.</summary>
    public void Dispose()
    {
        _detach?.Invoke();
        _detach = null;
        if (_recording)
        {
            _ = _recorder.Finish();
            _recording = false;
        }

        _pending = null;
        _work.Cancel();
        _work.Dispose();
        _work = new CancellationTokenSource();
    }

    private async Task TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken ct)
    {
        try
        {
            var raw = (await _transcriber.TranscribeAsync(audio, ct).ConfigureAwait(true)).Trim();
            if (raw.Length == 0)
            {
                return;
            }

            string? cleaned = null;
            if (_cleaner is not null)
            {
                cleaned = await _cleaner.CleanAsync(raw, ct).ConfigureAwait(true);
            }

            if (!ct.IsCancellationRequested)
            {
                TranscriptReady?.Invoke(this, new DictationResult(raw, string.IsNullOrWhiteSpace(cleaned) ? null : cleaned));
            }
        }
        catch (OperationCanceledException)
        {
            // Dictation was torn down while the transcript was being made. Nothing is placed.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _log?.Invoke($"Bazaar: dictation failed: {ex.Message}");
        }
    }
}
