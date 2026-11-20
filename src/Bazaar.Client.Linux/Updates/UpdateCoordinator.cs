using Bazaar.Client.Commands;
using Bazaar.Client.Updater.Linux;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// Runs the Linux updater for the head (doc 30 section 7): a check in the background at start, and an on-demand
/// "Check for updates" that stages an available release. It never restarts the process; an installed release applies on the
/// next restart. The notice is raised through <c>post</c> so a UI head can marshal it onto its UI thread.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    /// <summary>Id of the palette command that runs <see cref="CheckNowAsync"/>.</summary>
    public const string CheckCommandId = "update.check";

    private readonly ILinuxUpdater _updater;
    private readonly Action<Action> _post;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateCoordinator(ILinuxUpdater updater, Action<Action> post, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(log);
        _updater = updater;
        _post = post;
        _log = log;
    }

    /// <summary>The palette command for the on-demand check. Registered with the other local commands.</summary>
    public static Command CheckCommand { get; } =
        new(CheckCommandId, "Check for updates", "check for updates", ["update", "upgrade", "version"], CommandSource.Local);

    /// <summary>The latest notice for the user, or null when there is nothing to say.</summary>
    public string? Notice { get; private set; }

    /// <summary>Raised, through <c>post</c>, when <see cref="Notice"/> changes.</summary>
    public event Action<string?>? NoticeChanged;

    /// <summary>Starts the start-up check on the thread pool and returns at once. It only checks; it never installs.</summary>
    public Task StartBackgroundCheck(CancellationToken ct = default) => Task.Run(async () =>
    {
        try
        {
            UpdateResult result = await RunAsync(() => _updater.CheckAsync(ct), install: false).ConfigureAwait(false);
            _log($"update check: {result.Status} {result.Detail}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"update check failed: {ex.Message}");
        }
    }, CancellationToken.None);

    /// <summary>Checks now and, when a release is on offer, stages it. Returns the outcome shown to the user.</summary>
    public async Task<UpdateResult> CheckNowAsync(CancellationToken ct = default)
    {
        try
        {
            return await RunAsync(async () =>
            {
                UpdateResult check = await _updater.CheckAsync(ct).ConfigureAwait(false);
                return check.Status == UpdateStatus.Available ? await _updater.ApplyAsync(ct).ConfigureAwait(false) : check;
            }, install: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"update failed: {ex.Message}");
            SetNotice($"Update check failed: {ex.Message}");
            return new UpdateResult(UpdateStatus.DownloadFailed, null, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    private async Task<UpdateResult> RunAsync(Func<Task<UpdateResult>> step, bool install)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            UpdateResult result = await step().ConfigureAwait(false);
            SetNotice(Describe(result, install));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The user-facing line for a result. Quiet outcomes (no manifest, up to date on a background check) give null.</summary>
    public static string? Describe(UpdateResult result, bool install) => result.Status switch
    {
        UpdateStatus.Installed => $"Update {result.Version} is installed. It applies the next time you restart Bazaar.",
        UpdateStatus.Available => $"Update {result.Version} is available. Choose Check for updates to install it.",
        UpdateStatus.UpToDate => install ? "Bazaar is up to date." : null,
        UpdateStatus.NoManifest => install ? "Your company server offers no update." : null,
        _ => $"Update not installed: {result.Status}. {result.Detail}".TrimEnd(),
    };

    public void Dispose() => _gate.Dispose();

    private void SetNotice(string? notice)
    {
        Notice = notice;
        _post(() => NoticeChanged?.Invoke(notice));
    }
}
