using Velopack;
using Velopack.Locators;

namespace Bazaar.Client.Updates;

/// <summary>The real <see cref="IVelopackAdapter"/>. A new <see cref="UpdateManager"/> is made per check, because the feed and channel can change.</summary>
public sealed class VelopackAdapter : IVelopackAdapter
{
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public string? InstalledVersion
    {
        get
        {
            // IsInstalled is false for a dev run, so the version is only read for a real install.
            var probe = new UpdateManager("https://invalid.invalid/", null, null);
            return probe.IsInstalled ? probe.CurrentVersion?.ToString() : null;
        }
    }

    public async Task<string?> CheckAsync(Uri feed, string velopackChannel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ct.ThrowIfCancellationRequested();

        _manager = new UpdateManager(feed.AbsoluteUri, new UpdateOptions { ExplicitChannel = velopackChannel }, null);
        _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return _pending?.TargetFullRelease.Version.ToString();
    }

    public async Task<string> DownloadAsync(IProgress<int>? progress, CancellationToken ct)
    {
        if (_manager is null || _pending is null)
        {
            throw new InvalidOperationException("No update has been checked.");
        }

        await _manager.DownloadUpdatesAsync(_pending, progress is null ? null : progress.Report, ct).ConfigureAwait(false);
        // The app head calls VelopackApp.Build().Run() at startup, which sets the current locator.
        return Path.Combine(VelopackLocator.Current.PackagesDir!, _pending.TargetFullRelease.FileName);
    }

    public void ApplyAndRestart()
    {
        if (_manager is null || _pending is null)
        {
            throw new InvalidOperationException("No update has been downloaded.");
        }

        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
