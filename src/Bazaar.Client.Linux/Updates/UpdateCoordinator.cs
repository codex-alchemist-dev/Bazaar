using Bazaar.Client.Updater.Linux;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// Runs the Linux updater for the head (doc 30 section 7): one background check shortly after start, and an on-demand
/// "Check for updates". A release on offer is gated on the last welcome, then staged; it applies on the next restart, never to
/// the running process. Nothing here throws into the UI: every failure becomes a notice, raised through <c>post</c> so the head
/// can marshal it onto its UI thread. One run at a time.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    /// <summary>Id of the palette command that runs <see cref="CheckNowAsync"/>.</summary>
    public const string CheckCommandId = "update.check";

    private readonly ILinuxUpdater? _updater;
    private readonly string? _disabledReason;
    private readonly WelcomeCompatibilityHolder _compat;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _startDelay;
    private readonly Action<Action> _post;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Creates a coordinator. A null <paramref name="updater"/> means updates are off for <paramref name="disabledReason"/>.</summary>
    public UpdateCoordinator(
        ILinuxUpdater? updater,
        string? disabledReason,
        WelcomeCompatibilityHolder compat,
        TimeProvider clock,
        TimeSpan startDelay,
        Action<Action> post,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(compat);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(log);
        _updater = updater;
        _disabledReason = disabledReason ?? (updater is null ? "Updates are off." : null);
        _compat = compat;
        _clock = clock;
        _startDelay = startDelay;
        _post = post;
        _log = log;
    }

    /// <summary>The latest notice, or null when there is nothing to say.</summary>
    public UpdateNotice? Notice { get; private set; }

    /// <summary>Raised, through <c>post</c>, when <see cref="Notice"/> changes.</summary>
    public event Action<UpdateNotice?>? NoticeChanged;

    /// <summary>
    /// Starts the one start-up check on the thread pool and returns the running task at once. It waits the start delay first.
    /// A quiet outcome (up to date, updates off) sets no notice; a staged release or a failure does.
    /// </summary>
    public Task StartBackgroundCheck() => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(_startDelay, _clock, _stop.Token).ConfigureAwait(false);
            var notice = await RunAsync(_stop.Token).ConfigureAwait(false);
            if (notice.Kind is UpdateNoticeKind.ReadyAfterRestart or UpdateNoticeKind.Failed)
            {
                SetNotice(notice);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }, CancellationToken.None);

    /// <summary>Checks now and stages a release on offer. Always sets and returns a notice; never throws.</summary>
    public async Task<UpdateNotice> CheckNowAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        UpdateNotice notice;
        try
        {
            notice = await RunAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            notice = new UpdateNotice(UpdateNoticeKind.Failed, "The update check was cancelled.");
        }

        SetNotice(notice);
        return notice;
    }

    /// <summary>Releases the coordinator and cancels a pending check.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task<UpdateNotice> RunAsync(CancellationToken ct)
    {
        if (_updater is null)
        {
            return new UpdateNotice(UpdateNoticeKind.Disabled, _disabledReason!);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var check = await _updater.CheckAsync(ct).ConfigureAwait(false);
            _log($"update check: {check.Status} {check.Detail}");
            if (check.Status != UpdateStatus.Available)
            {
                return UpdateNotice.From(check);
            }

            // Gate on what the server said in its welcome before anything is downloaded (doc 02, doc 04).
            string? refusal = _compat.Refusal();
            if (refusal is not null)
            {
                _log($"update held back: {refusal}");
                return new UpdateNotice(UpdateNoticeKind.Failed, $"Update {check.Version} was not installed. {refusal}");
            }

            var applied = await _updater.ApplyAsync(ct).ConfigureAwait(false);
            _log($"update apply: {applied.Status} {applied.Detail}");
            return UpdateNotice.From(applied);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"update failed: {ex.Message}");
            return new UpdateNotice(UpdateNoticeKind.Failed, $"Update check failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetNotice(UpdateNotice notice)
    {
        Notice = notice;
        _post(() => NoticeChanged?.Invoke(notice));
    }
}
