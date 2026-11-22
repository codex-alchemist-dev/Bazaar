using System.Collections.Concurrent;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>
/// Adapts a QEMU/KVM guest to <see cref="IHypervisorConsole"/> through QMP: <c>screendump</c> for frames and
/// <c>input-send-event</c> for input (docs/design/59, sections 3 and 4). <see cref="DisplayTarget.BackendRef"/> is the domain name.
/// </summary>
/// <remarks>
/// Absolute pointer input needs the screen size, which QMP does not report. The console remembers the size of the last frame it
/// captured per domain and captures one first when it has none. Click and wheel presses are sent as separate commands from their
/// releases because a USB tablet only reports the button state at the end of a command (one sync per command).
/// </remarks>
public sealed class KvmConsole : IHypervisorConsole
{
    private const int _maxClickCount = 10;
    private const int _maxScrollSteps = 20;

    private readonly KvmOptions _options;
    private readonly IQmpTransport _transport;
    private readonly IScreendumpFileReader _reader;
    private readonly ConcurrentDictionary<string, (int Width, int Height)> _sizes = new(StringComparer.Ordinal);

    /// <summary>Creates the console.</summary>
    public KvmConsole(KvmOptions options, IQmpTransport transport, IScreendumpFileReader reader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(reader);
        _options = options;
        _transport = transport;
        _reader = reader;
    }

    /// <inheritdoc />
    public bool Supports(DisplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _options.Enabled && DomainName.IsValid(target.BackendRef);
    }

    /// <inheritdoc />
    public async Task<RawFrame> ReadFramebufferAsync(DisplayTarget target, int maxWidth, int maxHeight, CancellationToken ct)
    {
        string domain = RequireDomain(target);
        return await CaptureAsync(domain, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        string domain = RequireDomain(target);
        ArgumentNullException.ThrowIfNull(actions);

        (int Width, int Height) size = default;
        if (actions.Any(a => a is PointerMove or PointerClick or PointerScroll))
        {
            if (!_sizes.TryGetValue(domain, out size))
            {
                await CaptureAsync(domain, ct).ConfigureAwait(false);
                _sizes.TryGetValue(domain, out size);
            }

            if (size.Width < 1 || size.Height < 1)
                throw new DisplayException(DisplayFailure.SourceFailed, "The screen size of the VM is not known.");
        }

        // Build every command first so that an invalid action sends nothing.
        List<IReadOnlyList<QmpEvent>> commands = [];
        foreach (InputAction action in actions)
            AddCommands(commands, action, size.Width, size.Height);

        foreach (IReadOnlyList<QmpEvent> events in commands)
            await SendAsync(domain, QmpCommands.InputSendEvent(events), ct).ConfigureAwait(false);
    }

    /// <summary>Scales a guest pixel coordinate onto QMP's absolute axis range (0 to 32767), clamped.</summary>
    internal static int ScaleAxis(int pixel, int extent)
    {
        if (extent <= 1)
            return 0;
        long v = (long)Math.Clamp(pixel, 0, extent - 1) * QmpCommands.AbsMax / (extent - 1);
        return (int)Math.Clamp(v, 0, QmpCommands.AbsMax);
    }

    private static string RequireDomain(DisplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!DomainName.IsValid(target.BackendRef))
            throw new DisplayException(DisplayFailure.InvalidInput, "The VM name is not valid.");
        return target.BackendRef;
    }

    private async Task<RawFrame> CaptureAsync(string domain, CancellationToken ct)
    {
        (bool ok, byte[] data, string? error) = await TryDumpAsync(domain, "png", ct).ConfigureAwait(false);
        RawPixelFormat format = RawPixelFormat.Png;
        if (!ok && error is not null && error.Contains("format", StringComparison.OrdinalIgnoreCase))
        {
            // QEMU older than 7.1 has no format parameter and writes PPM.
            (ok, data, error) = await TryDumpAsync(domain, null, ct).ConfigureAwait(false);
            format = RawPixelFormat.Ppm;
        }

        if (!ok)
            throw new DisplayException(DisplayFailure.SourceFailed, $"QEMU screendump failed: {error}");

        bool parsed = format == RawPixelFormat.Png
            ? ImageHeader.TryReadPng(data, out int width, out int height)
            : ImageHeader.TryReadPpm(data, out width, out height);
        if (!parsed)
            throw new DisplayException(DisplayFailure.FrameMalformed, "The screendump file is not a valid image.");
        _sizes[domain] = (width, height);
        return new RawFrame(format, data, 0, 0);
    }

    private async Task<(bool Ok, byte[] Data, string? Error)> TryDumpAsync(string domain, string? format, CancellationToken ct)
    {
        // The path is read by QEMU on the KVM host, which is Linux, so it is joined with '/' whatever
        // system this code runs on. Path.Combine would use a backslash on Windows.
        string path = _options.ScreendumpDirectory.TrimEnd('/') + "/" + Guid.NewGuid().ToString("N") + (format is null ? ".ppm" : ".png");
        try
        {
            string response = await _transport.ExecuteAsync(domain, QmpCommands.Screendump(path, format), ct).ConfigureAwait(false);
            QmpResult result = QmpResult.Parse(response);
            if (!result.Ok)
                return (false, [], result.ErrorDescription);
            return (true, await _reader.ReadAsync(path, ct).ConfigureAwait(false), null);
        }
        finally
        {
            _reader.Delete(path);
        }
    }

    private async Task SendAsync(string domain, string commandJson, CancellationToken ct)
    {
        string response = await _transport.ExecuteAsync(domain, commandJson, ct).ConfigureAwait(false);
        QmpResult result = QmpResult.Parse(response);
        if (!result.Ok)
            throw new DisplayException(DisplayFailure.SourceFailed, $"QMP input failed: {result.ErrorDescription}");
    }

    private static void AddCommands(List<IReadOnlyList<QmpEvent>> commands, InputAction action, int width, int height)
    {
        switch (action)
        {
            case PointerMove m:
                commands.Add(Position(m.X, m.Y, width, height));
                break;
            case PointerClick c:
                AddClick(commands, c, width, height);
                break;
            case PointerScroll s:
                AddScroll(commands, s, width, height);
                break;
            case PointerButton b:
                commands.Add([QmpEvent.Btn(ButtonName(b.Button), b.Down)]);
                break;
            case KeyStroke k:
                commands.Add(KeyEvents(k));
                break;
            case TypeText t:
                AddText(commands, t.Text);
                break;
            default:
                throw new DisplayException(DisplayFailure.InvalidInput, "The input action is not supported.");
        }
    }

    private static List<QmpEvent> Position(int x, int y, int width, int height) =>
    [
        QmpEvent.Abs("x", ScaleAxis(x, width)),
        QmpEvent.Abs("y", ScaleAxis(y, height)),
    ];

    private static void AddClick(List<IReadOnlyList<QmpEvent>> commands, PointerClick click, int width, int height)
    {
        if (click.Count is < 1 or > _maxClickCount)
            throw new DisplayException(DisplayFailure.InvalidInput, $"A click count must be between 1 and {_maxClickCount}.");
        string button = ButtonName(click.Button);
        for (int i = 0; i < click.Count; i++)
        {
            List<QmpEvent> down = Position(click.X, click.Y, width, height);
            down.Add(QmpEvent.Btn(button, true));
            commands.Add(down);
            commands.Add([QmpEvent.Btn(button, false)]);
        }
    }

    private static void AddScroll(List<IReadOnlyList<QmpEvent>> commands, PointerScroll scroll, int width, int height)
    {
        List<QmpEvent> events = Position(scroll.X, scroll.Y, width, height);
        AddWheel(events, scroll.DeltaY, "wheel-up", "wheel-down");
        commands.Add(events);
    }

    private static void AddWheel(List<QmpEvent> events, int delta, string negative, string positive)
    {
        // A positive DeltaY scrolls down, as in browser wheel events.
        int steps = Math.Min(Math.Abs(delta), _maxScrollSteps);
        string button = delta < 0 ? negative : positive;
        for (int i = 0; i < steps; i++)
        {
            events.Add(QmpEvent.Btn(button, true));
            events.Add(QmpEvent.Btn(button, false));
        }
    }

    private static string ButtonName(PointerButtonKind button) => button switch
    {
        PointerButtonKind.Left => "left",
        PointerButtonKind.Middle => "middle",
        PointerButtonKind.Right => "right",
        _ => throw new DisplayException(DisplayFailure.InvalidInput, "The pointer button is not supported."),
    };

    private static List<QmpEvent> KeyEvents(KeyStroke stroke)
    {
        if (!QmpKeyTables.TryGetKey(stroke.Key, out string qcode))
            throw new DisplayException(DisplayFailure.InvalidInput, "The key is not on the allowlist.");
        string[] modifiers = [.. QmpKeyTables.ModifierQCodes(stroke.Modifiers)];
        List<QmpEvent> events = [];
        foreach (string m in modifiers)
            events.Add(QmpEvent.Key(m, true));
        events.Add(QmpEvent.Key(qcode, true));
        events.Add(QmpEvent.Key(qcode, false));
        for (int i = modifiers.Length - 1; i >= 0; i--)
            events.Add(QmpEvent.Key(modifiers[i], false));
        return events;
    }

    private static void AddText(List<IReadOnlyList<QmpEvent>> commands, string text)
    {
        List<QmpEvent> current = [];
        foreach (char c in text)
        {
            if (!QmpKeyTables.TryGetChar(c, out string qcode, out bool shift))
                throw new DisplayException(
                    DisplayFailure.InvalidInput,
                    "Text typed through the hypervisor is limited to printable US-ASCII, newline and tab: there is no IME path, so other characters cannot be typed.");

            int needed = shift ? 4 : 2;
            if (current.Count + needed > QmpCommands.MaxEventsPerCommand)
            {
                commands.Add(current);
                current = [];
            }

            if (shift)
                current.Add(QmpEvent.Key("shift", true));
            current.Add(QmpEvent.Key(qcode, true));
            current.Add(QmpEvent.Key(qcode, false));
            if (shift)
                current.Add(QmpEvent.Key("shift", false));
        }

        if (current.Count > 0)
            commands.Add(current);
    }
}
