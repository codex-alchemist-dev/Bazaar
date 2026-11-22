using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.Android;

/// <summary>
/// Builds the fixed argument lists for the Android tools (doc 62 section 5.1). Every variable part is validated first, and nothing
/// goes through a shell. Pure, so it is unit tested without an SDK.
/// </summary>
public static class AndroidArgv
{
    /// <summary>Emulator arguments for a headless guest on one console port.</summary>
    public static IReadOnlyList<string> Emulator(string avdName, int port, string? loadSnapshot = null)
    {
        var args = new List<string>
        {
            "-avd", DeviceIdentifiers.Name(avdName),
            "-port", CheckPort(port).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-no-window", "-no-audio", "-no-boot-anim", "-no-snapshot-save",
        };
        if (loadSnapshot is not null)
        {
            args.AddRange(["-snapshot", DeviceIdentifiers.Name(loadSnapshot)]);
        }

        return args;
    }

    /// <summary>avdmanager arguments that create one AVD from the pinned system image.</summary>
    public static IReadOnlyList<string> CreateAvd(string avdName, string systemImagePackage, string device) =>
        ["create", "avd", "--force", "--name", DeviceIdentifiers.Name(avdName), "--package", systemImagePackage, "--device", DeviceIdentifiers.Name(device)];

    /// <summary>adb arguments are prefixed with the emulator serial, so a call can only reach its own emulator.</summary>
    public static string Serial(int port) => $"emulator-{CheckPort(port)}";

    public static IReadOnlyList<string> Install(int port, string apkPath) =>
        ["-s", Serial(port), "install", "-r", apkPath];

    public static IReadOnlyList<string> Launch(int port, string appId) =>
        ["-s", Serial(port), "shell", "monkey", "-p", DeviceIdentifiers.AppId(appId), "-c", "android.intent.category.LAUNCHER", "1"];

    public static IReadOnlyList<string> Screencap(int port) => ["-s", Serial(port), "exec-out", "screencap", "-p"];

    public static IReadOnlyList<string> Input(int port, DeviceInput input) => input switch
    {
        DeviceInput.Tap t => ["-s", Serial(port), "shell", "input", "tap", NonNegative(t.X), NonNegative(t.Y)],
        DeviceInput.Key k => ["-s", Serial(port), "shell", "input", "keyevent", NonNegative(k.KeyCode)],
        DeviceInput.Text x => ["-s", Serial(port), "shell", "input", "text", EncodeText(x.Value)],
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };

    /// <summary><c>input text</c> treats a space as an argument break and % as an escape; both are encoded, and other shell-special characters are refused.</summary>
    public static string EncodeText(string text)
    {
        DeviceIdentifiers.InputText(text);
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (c == ' ')
            {
                sb.Append("%s");
            }
            else if (char.IsAsciiLetterOrDigit(c) || c is '.' or ',' or '-' or '_' or '@' or ':' or '/' or '+' or '=')
            {
                sb.Append(c);
            }
            else
            {
                throw new ArgumentException($"Character U+{(int)c:X4} cannot be typed safely.", nameof(text));
            }
        }

        return sb.ToString();
    }

    private static int CheckPort(int port)
    {
        if (port is < 5554 or > 5584 || port % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Emulator console ports are even numbers from 5554 to 5584.");
        }

        return port;
    }

    private static string NonNegative(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
