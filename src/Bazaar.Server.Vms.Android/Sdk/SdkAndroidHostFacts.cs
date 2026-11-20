using System.Runtime.InteropServices;
using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.Android.Sdk;

/// <summary>
/// Probes whether the emulator can run here (doc 62 section 5.1): emulator and adb exist under SdkRoot, and the host has acceleration
/// (<c>/dev/kvm</c> readable on Linux, <c>WinHvPlatform.dll</c> on Windows). Fails closed. File probes are injected for tests.
/// </summary>
public sealed class SdkAndroidHostFacts : IAndroidHostFacts
{
    public const string Feature = "android-emulator";

    private readonly AndroidOptions _options;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _deviceReadable;
    private readonly Func<string> _systemDirectory;
    private readonly string? _osOverride;

    public SdkAndroidHostFacts(
        AndroidOptions options,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? deviceReadable = null,
        Func<string>? systemDirectory = null,
        string? osOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _fileExists = fileExists ?? File.Exists;
        _deviceReadable = deviceReadable ?? CanRead;
        _systemDirectory = systemDirectory ?? (() => Environment.SystemDirectory);
        _osOverride = osOverride;
    }

    /// <inheritdoc />
    public HostFacts Current()
    {
        var os = _osOverride ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other");
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "other",
        };

        var features = new HashSet<string>(StringComparer.Ordinal);
        if (Ready(os))
        {
            features.Add(Feature);
        }

        return new HostFacts(os, arch, features);
    }

    private bool Ready(string os)
    {
        AndroidSdkPaths paths;
        try
        {
            paths = new AndroidSdkPaths(_options.SdkRoot, os == "windows");
            if (!_fileExists(paths.Emulator) || !_fileExists(paths.Adb))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return os switch
        {
            "linux" => _deviceReadable("/dev/kvm"),
            "windows" => _fileExists(Path.Combine(_systemDirectory(), "WinHvPlatform.dll")),
            "macos" => true,
            _ => false,
        };
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
