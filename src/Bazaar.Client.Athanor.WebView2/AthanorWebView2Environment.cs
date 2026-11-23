using System.Collections.Concurrent;
using Microsoft.Web.WebView2.Core;

namespace Bazaar.Client.Athanor.WebView2;

/// <summary>
/// The shared WebView2 environment (doc 28 process model, doc 57 §3a): one per user-data folder, created once and reused
/// by every editor window, so a second window does not pay the cold start again. Call <see cref="PreWarmAsync"/> at client
/// start to take the environment cost (about a second in spike S12) before the first window needs it.
/// </summary>
public static class AthanorWebView2Environment
{
    private static readonly ConcurrentDictionary<(string Folder, int Thread), Task<CoreWebView2Environment>> _environments = new();

    /// <summary>True when a WebView2 runtime is installed on this machine.</summary>
    public static bool IsRuntimeAvailable() => GetRuntimeVersion() is not null;

    /// <summary>The installed WebView2 runtime version, or null when there is none.</summary>
    public static string? GetRuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return null; // the loader is missing or not for this architecture
        }
    }

    /// <summary>Creates (or returns) the environment for <paramref name="options"/>. Must run on the UI thread that will own the controllers.</summary>
    public static Task<CoreWebView2Environment> GetAsync(WebView2HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        // A WebView2 environment belongs to the COM apartment that created it, so the cache is per UI thread. A client has
        // one UI thread, so in practice this is one environment per user-data folder.
        string folder = Path.GetFullPath(options.UserDataFolder);
        var key = (folder.ToUpperInvariant(), Environment.CurrentManagedThreadId);
        return _environments.GetOrAdd(key, static (_, path) => Create(path), folder);
    }

    /// <summary>Starts creating the environment so the first window finds it ready. Safe to call more than once.</summary>
    public static Task PreWarmAsync(WebView2HostOptions options) => GetAsync(options);

    private static async Task<CoreWebView2Environment> Create(string folder)
    {
        Directory.CreateDirectory(folder);
        return await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: folder, options: null).ConfigureAwait(true);
    }
}
