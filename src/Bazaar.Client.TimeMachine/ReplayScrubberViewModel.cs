using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.TimeMachine;

/// <summary>How the scrubber opens (setting <c>replay.default_view</c>, doc 26 section 4).</summary>
public enum ReplayViewMode
{
    /// <summary>The live GC. The read-only banner shows only once the viewer scrubs back.</summary>
    Current,

    /// <summary>The scrubber is engaged: the read-only banner shows at every seq, the head included.</summary>
    History,
}

/// <summary>
/// The replay scrubber pane (doc 26 section 4, client UX rule 5). Holds a cursor <c>(gc, seq)</c> and the frame the server
/// rendered for it. Read-only: nothing here edits a message, a task or a memory, and the only write is <see cref="ForkAsync"/>.
/// Pane model: <c>{ "gcId": "guid", "headSeq": 120, "defaultView": "current" | "history" }</c>; <c>defaultView</c> is the install
/// value of <c>replay.default_view</c> and may be left out (then <see cref="ReplayViewMode.Current"/>). A bad model raises
/// <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class ReplayScrubberViewModel : IPaneViewModel
{
    /// <summary>The pane kind this view model is registered under.</summary>
    public const string PaneKind = "bazaar.replay";

    private readonly IReplayClient? _client;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<long, Guid> _forkRequestIds = [];
    private readonly IReplayPreferences? _preferences;
    private ReplayViewMode? _viewerOverride;
    private CancellationTokenSource? _playing;
    private int _requestCounter;

    public ReplayScrubberViewModel(
        Guid gcId,
        long headSeq,
        ReplayViewMode defaultView,
        IReplayClient? client,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IReplayPreferences? preferences = null)
    {
        _preferences = preferences;
        _viewerOverride = preferences?.GetViewerOverride();
        GcId = gcId;
        HeadSeq = Math.Max(0, headSeq);
        CursorSeq = HeadSeq;
        InstallDefaultView = defaultView;
        _client = client;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Builds the view model from a pane descriptor model.</summary>
    public static ReplayScrubberViewModel FromModel(JsonElement model, IReplayClient? client, IReplayPreferences? preferences = null)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("the replay pane model must be an object");
        }

        if (!Guid.TryParse(PaneValues.RequiredString(model, "gcId"), out var gcId))
        {
            throw new PaneModelException("model field 'gcId' must be a GUID");
        }

        if (!model.TryGetProperty("headSeq", out var head) || head.ValueKind != JsonValueKind.Number || !head.TryGetInt64(out var headSeq) || headSeq < 0)
        {
            throw new PaneModelException("model field 'headSeq' must be a non-negative integer");
        }

        var mode = ReplayViewMode.Current;
        if (model.TryGetProperty("defaultView", out _))
        {
            mode = PaneValues.RequiredString(model, "defaultView") switch
            {
                "current" => ReplayViewMode.Current,
                "history" => ReplayViewMode.History,
                var other => throw new PaneModelException($"unknown defaultView '{other}'"),
            };
        }

        return new ReplayScrubberViewModel(gcId, headSeq, mode, client, preferences: preferences);
    }

    public string Kind => PaneKind;

    public string Title => "Time Machine · Replay";

    public Guid GcId { get; }

    /// <summary>The newest seq the viewer may scrub to. Refreshed from each frame the server returns.</summary>
    public long HeadSeq { get; private set; }

    /// <summary>The seq the pane shows. After a seek it is the seq the server actually rendered.</summary>
    public long CursorSeq { get; private set; }

    /// <summary>The frame at the cursor, or null before the first load.</summary>
    public ReplayFrameView? Frame { get; private set; }

    /// <summary>The install value of <c>replay.default_view</c>.</summary>
    public ReplayViewMode InstallDefaultView { get; }

    /// <summary>The viewer's own choice, or null to follow the install value. Any member may set it (client preference).</summary>
    public ReplayViewMode? ViewerOverride => _viewerOverride;

    /// <summary>The mode in effect: the viewer's override, else the install default.</summary>
    public ReplayViewMode Mode => _viewerOverride ?? InstallDefaultView;

    /// <summary>True while the pane is showing history, by mode or because the cursor is behind the head.</summary>
    public bool IsReplaying => Mode == ReplayViewMode.History || CursorSeq < HeadSeq;

    /// <summary>The read-only banner naming the viewed seq, or null when the pane shows the live GC.</summary>
    public string? BannerText =>
        IsReplaying
            ? $"Read-only history: viewing seq {Num(CursorSeq)} of {Num(HeadSeq)}. Nothing here can be edited."
            : null;

    public bool IsLoading { get; private set; }

    public bool IsPlaying => _playing is not null;

    /// <summary>The server's refusal or failure text from the last call, or null.</summary>
    public string? ErrorText { get; private set; }

    /// <summary>What the last fork made, or null.</summary>
    public ReplayForkOutcome? LastFork { get; private set; }

    /// <summary>Text for the last fork, or null.</summary>
    public string? ForkNotice { get; private set; }

    /// <summary>True when a client is bound. Without one the pane shows its empty state.</summary>
    public bool HasClient => _client is not null;

    public bool CanStepBack => HasClient && !IsLoading && CursorSeq > 0;

    public bool CanStepForward => HasClient && !IsLoading && CursorSeq < HeadSeq;

    public bool CanPlay => HasClient && !IsLoading && !IsPlaying && HeadSeq > 0;

    public bool CanFork => HasClient && !IsLoading && Frame is not null;

    /// <summary>Raised after any state change. The view rebuilds from the view model.</summary>
    public event Action? Changed;

    /// <summary>Raised once per successful fork, so a head can open the new GC.</summary>
    public event Action<ReplayForkOutcome>? Forked;

    /// <summary>Sets or clears the viewer's override of <c>replay.default_view</c>. Null follows the install value again.</summary>
    public void SetViewerOverride(ReplayViewMode? mode)
    {
        _viewerOverride = mode;
        _preferences?.SetViewerOverride(mode);
        Changed?.Invoke();
    }

    /// <summary>Loads the frame at the cursor (the head on open).</summary>
    public Task LoadAsync(CancellationToken ct = default) => SeekAsync(CursorSeq, ct);

    /// <summary>Moves the cursor to <paramref name="seq"/>, clamped to the range 0 to the head, and loads that frame.</summary>
    public async Task SeekAsync(long seq, CancellationToken ct = default)
    {
        if (_client is null)
        {
            return;
        }

        var target = Math.Clamp(seq, 0, HeadSeq);
        var ticket = ++_requestCounter;
        IsLoading = true;
        ErrorText = null;
        Changed?.Invoke();
        try
        {
            var frame = await _client.GetFrameAsync(GcId, target, ct).ConfigureAwait(true);
            if (ticket != _requestCounter)
            {
                return; // a newer seek is in flight; its frame wins
            }

            Frame = frame;
            HeadSeq = Math.Max(frame.HeadSeq, frame.EffectiveSeq);
            CursorSeq = frame.EffectiveSeq;
        }
        catch (ReplayClientException ex)
        {
            if (ticket == _requestCounter)
            {
                ErrorText = ex.Message;
            }
        }
        finally
        {
            if (ticket == _requestCounter)
            {
                IsLoading = false;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Moves the cursor by <paramref name="delta"/> seqs (negative steps back).</summary>
    public Task StepAsync(long delta, CancellationToken ct = default) => SeekAsync(CursorSeq + delta, ct);

    /// <summary>
    /// Plays forward one seq per <paramref name="interval"/> until the head or <see cref="Pause"/>. Starting at the head
    /// restarts from seq 0. Replay never re-executes anything: each step only loads the next frame.
    /// </summary>
    public async Task PlayAsync(TimeSpan interval, CancellationToken ct = default)
    {
        if (_client is null || IsPlaying)
        {
            return;
        }

        using var playing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _playing = playing;
        Changed?.Invoke();
        try
        {
            if (CursorSeq >= HeadSeq)
            {
                await SeekAsync(0, playing.Token).ConfigureAwait(true);
            }

            while (!playing.IsCancellationRequested && ErrorText is null && CursorSeq < HeadSeq)
            {
                await _delay(interval, playing.Token).ConfigureAwait(true);
                await SeekAsync(CursorSeq + 1, playing.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Pause or the caller's token ended the run; the cursor stays where it got to.
        }
        finally
        {
            _playing = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Stops <see cref="PlayAsync"/> where it is.</summary>
    public void Pause() => _playing?.Cancel();

    /// <summary>
    /// Forks a project GC at the viewed seq. Asks again with the same request id for the same seq, so a retry after a
    /// failure never makes a second fork. Copies no messages and changes nothing in the source GC.
    /// </summary>
    public async Task ForkAsync(CancellationToken ct = default)
    {
        if (_client is null || Frame is null)
        {
            return;
        }

        var seq = CursorSeq;
        if (!_forkRequestIds.TryGetValue(seq, out var requestId))
        {
            requestId = Guid.NewGuid();
            _forkRequestIds[seq] = requestId;
        }

        ErrorText = null;
        try
        {
            var outcome = await _client.ForkAsync(new ReplayForkRequest(requestId, GcId, seq), ct).ConfigureAwait(true);
            LastFork = outcome;
            ForkNotice = outcome.Created
                ? $"Forked a new project GC at seq {Num(outcome.SourceSeq)}. No messages were copied."
                : $"This point was already forked at seq {Num(outcome.SourceSeq)}; the earlier fork is shown.";
            Changed?.Invoke();
            Forked?.Invoke(outcome);
        }
        catch (ReplayClientException ex)
        {
            ErrorText = ex.Message;
            Changed?.Invoke();
        }
    }

    private static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
