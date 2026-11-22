using System.Globalization;
using System.Text;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.HyperV;

/// <summary>
/// Adapts a Hyper-V guest to <see cref="IHypervisorConsole"/> through the WMI virtualization namespace, driven by constant
/// PowerShell scripts (docs/design/59, sections 3 and 4). <see cref="DisplayTarget.BackendRef"/> is the VM's name. The module only
/// builds script parameters and parses output; the actual PowerShell run sits behind <see cref="IHyperVScriptRunner"/>.
/// </summary>
/// <remarks>
/// UNVERIFIED: spike S-DSP. Live behaviour (thumbnail on Linux guests, absolute mouse scaling, scroll units, keyboard layout) has
/// not been tested on a Hyper-V host. The calls need Hyper-V administrator rights.
/// </remarks>
public sealed class HyperVConsole : IHypervisorConsole
{
    private const int _maxTextPerCall = 512;
    private const int _maxClickCount = 10;

    private readonly HyperVOptions _options;
    private readonly IHyperVScriptRunner _runner;
    private readonly Func<bool> _isWindows;

    /// <summary>Creates the console.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="runner">Runs the constant scripts.</param>
    /// <param name="isWindows">Whether the host is Windows. The default asks the operating system; tests inject it.</param>
    public HyperVConsole(HyperVOptions options, IHyperVScriptRunner runner, Func<bool>? isWindows = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        _options = options;
        _runner = runner;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    /// <inheritdoc />
    public bool Supports(DisplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _options.Enabled && VmName.IsValid(target.BackendRef) && _isWindows();
    }

    /// <inheritdoc />
    public async Task<RawFrame> ReadFramebufferAsync(DisplayTarget target, int maxWidth, int maxHeight, CancellationToken ct)
    {
        string name = RequireName(target);
        int width = Clamp(Math.Min(maxWidth, _options.ThumbnailWidth));
        int height = Clamp(Math.Min(maxHeight, _options.ThumbnailHeight));
        var parameters = new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Width"] = width.ToString(CultureInfo.InvariantCulture),
            ["Height"] = height.ToString(CultureInfo.InvariantCulture),
        };
        string output = await _runner.RunAsync(HyperVScripts.Thumbnail, parameters, ct).ConfigureAwait(false);
        return Parse(output, width, height);
    }

    /// <inheritdoc />
    public async Task SendInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        string name = RequireName(target);
        ArgumentNullException.ThrowIfNull(actions);

        // Plan every run first so that an invalid action sends nothing. Consecutive actions for one device share one run.
        List<(string Script, string Steps)> runs = [];
        List<KeyStep> keys = [];
        List<MouseStep> mouse = [];
        foreach (InputAction action in actions)
        {
            if (action is KeyStroke or TypeText)
            {
                Flush(runs, mouse: mouse);
                AddKeys(keys, action);
            }
            else
            {
                Flush(runs, keys: keys);
                AddMouse(mouse, action);
            }
        }

        Flush(runs, keys: keys);
        Flush(runs, mouse: mouse);

        foreach ((string script, string steps) in runs)
            await _runner.RunAsync(script, new Dictionary<string, string> { ["Name"] = name, ["Steps"] = steps }, ct).ConfigureAwait(false);
    }

    private static void Flush(List<(string Script, string Steps)> runs, List<KeyStep>? keys = null, List<MouseStep>? mouse = null)
    {
        if (keys is { Count: > 0 })
        {
            runs.Add((HyperVScripts.Keys, HyperVSteps.ToJson(keys)));
            keys.Clear();
        }

        if (mouse is { Count: > 0 })
        {
            runs.Add((HyperVScripts.Mouse, HyperVSteps.ToJson(mouse)));
            mouse.Clear();
        }
    }

    private static string RequireName(DisplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!VmName.IsValid(target.BackendRef))
            throw new DisplayException(DisplayFailure.InvalidInput, "The VM name is not valid.");
        return target.BackendRef;
    }

    private static int Clamp(int size) => Math.Clamp(size, 1, ushort.MaxValue);

    private static RawFrame Parse(string output, int width, int height)
    {
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] size = lines.Length >= 2 ? lines[0].Split(' ') : [];
        if (size.Length != 2
            || !int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
            || !int.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            || w != width || h != height)
            throw new DisplayException(DisplayFailure.FrameMalformed, "The thumbnail output has no valid size line.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(lines[1]);
        }
        catch (FormatException e)
        {
            throw new DisplayException(DisplayFailure.FrameMalformed, "The thumbnail data is not valid base64.", e);
        }

        if (bytes.LongLength != (long)w * h * 2)
            throw new DisplayException(DisplayFailure.FrameMalformed, "The thumbnail length does not match width x height x 2.");
        return new RawFrame(RawPixelFormat.Rgb565, bytes, w, h);
    }

    private static void AddKeys(List<KeyStep> steps, InputAction action)
    {
        if (action is KeyStroke stroke)
        {
            if (!HyperVKeyTable.TryGetKey(stroke.Key, out int code))
                throw new DisplayException(DisplayFailure.InvalidInput, "The key is not on the allowlist.");
            int[] modifiers = [.. HyperVKeyTable.ModifierCodes(stroke.Modifiers)];
            foreach (int m in modifiers)
                steps.Add(KeyStep.Press(m));
            steps.Add(KeyStep.Key(code));
            for (int i = modifiers.Length - 1; i >= 0; i--)
                steps.Add(KeyStep.Release(modifiers[i]));
            return;
        }

        var sb = new StringBuilder();
        foreach (char c in ((TypeText)action).Text)
        {
            if (c == '\n' || c == '\t')
            {
                FlushText(steps, sb);
                steps.Add(KeyStep.Key(c == '\n' ? HyperVKeyTable.VkReturn : HyperVKeyTable.VkTab));
            }
            else if (c is >= ' ' and <= '~')
            {
                sb.Append(c);
                if (sb.Length == _maxTextPerCall)
                    FlushText(steps, sb);
            }
            else
            {
                throw new DisplayException(
                    DisplayFailure.InvalidInput,
                    "Text typed through the hypervisor is limited to printable ASCII, newline and tab: Msvm_Keyboard has no Unicode path here.");
            }
        }

        FlushText(steps, sb);
    }

    private static void FlushText(List<KeyStep> steps, StringBuilder sb)
    {
        if (sb.Length == 0)
            return;
        steps.Add(KeyStep.Type(sb.ToString()));
        sb.Clear();
    }

    private static void AddMouse(List<MouseStep> steps, InputAction action)
    {
        switch (action)
        {
            case PointerMove m:
                steps.Add(MoveStep(m.X, m.Y));
                break;
            case PointerClick c:
                if (c.Count is < 1 or > _maxClickCount)
                    throw new DisplayException(DisplayFailure.InvalidInput, $"A click count must be between 1 and {_maxClickCount}.");
                steps.Add(MoveStep(c.X, c.Y));
                for (int i = 0; i < c.Count; i++)
                    steps.Add(MouseStep.Click(ButtonIndex(c.Button)));
                break;
            case PointerScroll s:
                steps.Add(MoveStep(s.X, s.Y));
                if (s.DeltaY != 0)
                    steps.Add(MouseStep.Scroll(s.DeltaY));
                break;
            case PointerButton:
                throw new DisplayException(DisplayFailure.InvalidInput, "Hyper-V can only click a pointer button, not press or release it alone.");
            default:
                throw new DisplayException(DisplayFailure.InvalidInput, "The input action is not supported.");
        }
    }

    private static MouseStep MoveStep(int x, int y) =>
        MouseStep.Move(Math.Clamp(x, 0, DisplayLimits.CeilingWidth), Math.Clamp(y, 0, DisplayLimits.CeilingHeight));

    private static int ButtonIndex(PointerButtonKind button) => button switch
    {
        PointerButtonKind.Left => 1,
        PointerButtonKind.Right => 2,
        PointerButtonKind.Middle => 3,
        _ => throw new DisplayException(DisplayFailure.InvalidInput, "The pointer button is not supported."),
    };
}
