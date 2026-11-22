namespace Bazaar.Server.Vms.Android.Sdk;

/// <summary>Resolves the SDK tool paths from <c>SdkRoot</c> only. Any result outside the root is refused.</summary>
public sealed class AndroidSdkPaths
{
    private readonly string _root;
    private readonly bool _windows;

    public AndroidSdkPaths(string sdkRoot, bool windows)
    {
        if (string.IsNullOrWhiteSpace(sdkRoot) || !Path.IsPathRooted(sdkRoot))
        {
            throw new ArgumentException("SdkRoot must be an absolute path.", nameof(sdkRoot));
        }

        _root = Path.GetFullPath(sdkRoot);
        _windows = windows;
    }

    public string Emulator => Resolve("emulator", _windows ? "emulator.exe" : "emulator");

    public string Adb => Resolve("platform-tools", _windows ? "adb.exe" : "adb");

    public string AvdManager => Resolve("cmdline-tools", "latest", "bin", _windows ? "avdmanager.bat" : "avdmanager");

    /// <summary>Combines segments under the SDK root and refuses a result that escapes it.</summary>
    public string Resolve(params string[] segments)
    {
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || Path.IsPathRooted(segment) || segment.Contains('\0'))
            {
                throw new ArgumentException("Path segments must be relative.", nameof(segments));
            }
        }

        var full = Path.GetFullPath(Path.Combine([_root, .. segments]));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        var comparison = _windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(prefix, comparison))
        {
            throw new ArgumentException("Path escapes the SDK root.", nameof(segments));
        }

        return full;
    }
}
