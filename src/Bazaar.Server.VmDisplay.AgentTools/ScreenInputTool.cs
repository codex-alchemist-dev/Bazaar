using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.AgentTools;

/// <summary>
/// The <c>screen.input</c> agent tool (docs/design/59, section 4). It sends keyboard and pointer input to the calling agent's own VM or
/// browser session through <see cref="IGuestDisplay"/>, which applies the gate, the rate limit and the audit row. The target comes from
/// the dispatcher's <see cref="ToolCallContext"/>, never from the arguments. Pointer coordinates are in the frame the agent looked at
/// (<c>frame_width</c> by <c>frame_height</c>); when <see cref="IDisplayNativeSize"/> knows the display's native size they are scaled to
/// it, otherwise they pass through unchanged. The tool acts inside the agent's own sandbox, so it runs on a tainted session too
/// (docs/design/15 gates outward high-impact actions, and a VM keystroke is not one).
/// </summary>
public sealed class ScreenInputTool(
    IGuestDisplay display,
    IAgentDisplayTargets targets,
    ScreenToolsOptions options,
    IDisplayNativeSize? nativeSize = null) : IContextualTool
{
    /// <summary>The exact registered name.</summary>
    public const string ToolName = "screen.input";

    /// <summary>What a model is told about the tool.</summary>
    public static ModelToolDefinition Definition { get; } = new(
        ToolName,
        "Sends keyboard and mouse input to your own Linux VM, Windows VM or browser session. Pass the width and height of the " +
        "screenshot you are looking at as frame_width and frame_height; x and y are pixels in that screenshot. Actions run in order. " +
        "Action fields by type: type_text {text}; key {key, modifiers?}; move {x, y}; click {x, y, button?, count?}; " +
        "button_down and button_up {button?}; scroll {x, y, delta_x?, delta_y?}. Keys are names such as Enter, Tab, Escape, " +
        "ArrowLeft, KeyA, Digit1, F5. Take a new screenshot afterwards to see the result.",
        """
        {
          "type": "object",
          "properties": {
            "target": { "type": "string", "enum": ["linux", "windows", "browser"], "description": "Which of your displays to drive." },
            "source": { "type": "string", "enum": ["hypervisor", "guest", "browser"], "description": "Force one input source. Leave out to let the server choose. The browser target only uses browser." },
            "frame_width": { "type": "integer", "minimum": 1, "maximum": 3840, "description": "Width of the screenshot the coordinates refer to." },
            "frame_height": { "type": "integer", "minimum": 1, "maximum": 2160, "description": "Height of the screenshot the coordinates refer to." },
            "actions": {
              "type": "array",
              "minItems": 1,
              "maxItems": 50,
              "description": "The input actions, in order. The server may allow fewer than 50 per call.",
              "items": {
                "type": "object",
                "properties": {
                  "type": { "type": "string", "enum": ["type_text", "key", "move", "click", "button_down", "button_up", "scroll"] },
                  "text": { "type": "string", "description": "type_text: the text to type." },
                  "key": { "type": "string", "description": "key: the key name, for example Enter or KeyA." },
                  "modifiers": { "type": "array", "items": { "type": "string", "enum": ["shift", "control", "alt", "meta"] }, "description": "key: modifiers held during the key." },
                  "x": { "type": "integer", "minimum": 0, "description": "move, click, scroll: horizontal pixel in the screenshot." },
                  "y": { "type": "integer", "minimum": 0, "description": "move, click, scroll: vertical pixel in the screenshot." },
                  "button": { "type": "string", "enum": ["left", "middle", "right"], "description": "click, button_down, button_up: default left." },
                  "count": { "type": "integer", "minimum": 1, "maximum": 3, "description": "click: number of clicks, default 1." },
                  "delta_x": { "type": "integer", "minimum": -10000, "maximum": 10000, "description": "scroll: horizontal amount." },
                  "delta_y": { "type": "integer", "minimum": -10000, "maximum": 10000, "description": "scroll: vertical amount, positive scrolls down." }
                },
                "required": ["type"],
                "additionalProperties": false
              }
            }
          },
          "required": ["target", "frame_width", "frame_height", "actions"],
          "additionalProperties": false
        }
        """);

    private const int _maxScrollDelta = 10_000;
    private const int _maxClickCount = 3;

    private readonly IGuestDisplay _display = display ?? throw new ArgumentNullException(nameof(display));
    private readonly IAgentDisplayTargets _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    private readonly ScreenToolsOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IDisplayNativeSize? _nativeSize = nativeSize;

    /// <summary>
    /// Scales one coordinate from frame space to native space: round-half-away-from-zero of <c>value * native / frame</c>, clamped to
    /// <c>native - 1</c>. The value must already be inside the frame.
    /// </summary>
    public static int Scale(int value, int frame, int native)
    {
        if (frame == native)
            return value;
        long scaled = (long)Math.Round((double)value * native / frame, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(scaled, 0L, native - 1L);
    }

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        AgentDisplayKind kind;
        DisplaySourceKind? source;
        int frameWidth;
        int frameHeight;
        JsonElement rawActions;
        try
        {
            if (args.ValueKind != JsonValueKind.Object)
                throw new ScreenToolSupport.ArgumentProblem("Arguments must be a JSON object.");
            ScreenToolSupport.RejectUnknown(args, "The arguments", "target", "source", "frame_width", "frame_height", "actions");
            (kind, source) = ScreenToolSupport.ReadTargetAndSource(args);
            frameWidth = ScreenToolSupport.ReadInt(args, "frame_width", 1, DisplayLimits.CeilingWidth);
            frameHeight = ScreenToolSupport.ReadInt(args, "frame_height", 1, DisplayLimits.CeilingHeight);
            if (!args.TryGetProperty("actions", out rawActions) || rawActions.ValueKind != JsonValueKind.Array)
                throw new ScreenToolSupport.ArgumentProblem("actions must be an array.");
            int count = rawActions.GetArrayLength();
            if (count == 0)
                throw new ScreenToolSupport.ArgumentProblem("actions must not be empty.");
            if (count > _options.MaxActionsPerCall)
                throw new ScreenToolSupport.ArgumentProblem($"actions has {count} entries, above the limit of {_options.MaxActionsPerCall} per call.");
        }
        catch (ScreenToolSupport.ArgumentProblem problem)
        {
            return ScreenToolSupport.Refusal("invalid_arguments", problem.Message);
        }

        DisplayTarget? target = ScreenToolSupport.ResolveOwn(_targets, context.AgentId, kind, out string? refusal);
        if (target is null)
            return refusal!;

        // Scaling only happens when the native size is known and sane. Otherwise coordinates pass through unchanged.
        int nativeWidth = frameWidth;
        int nativeHeight = frameHeight;
        if (_nativeSize?.TryGet(target) is (int w, int h) && w >= 1 && h >= 1 && w <= DisplayLimits.CeilingWidth && h <= DisplayLimits.CeilingHeight)
        {
            nativeWidth = w;
            nativeHeight = h;
        }

        var actions = new List<InputAction>(rawActions.GetArrayLength());
        try
        {
            int index = 0;
            foreach (JsonElement raw in rawActions.EnumerateArray())
            {
                actions.Add(ParseAction(raw, index, frameWidth, frameHeight, nativeWidth, nativeHeight));
                index++;
            }
        }
        catch (ScreenToolSupport.ArgumentProblem problem)
        {
            return ScreenToolSupport.Refusal("invalid_arguments", problem.Message);
        }

        try
        {
            await _display.SendInputAsync(new InputBatch(target, actions, source), cancellationToken).ConfigureAwait(false);
        }
        catch (DisplayException ex)
        {
            return ScreenToolSupport.Failure(ex);
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["status"] = "sent",
            ["actions"] = actions.Count,
        }.ToJsonString();
    }

    private InputAction ParseAction(JsonElement raw, int index, int fw, int fh, int nw, int nh)
    {
        string where = $"actions[{index}]";
        if (raw.ValueKind != JsonValueKind.Object)
            throw new ScreenToolSupport.ArgumentProblem($"{where} must be an object.");

        string type = ScreenToolSupport.OptionalString(raw, "type", 16)!;
        switch (type)
        {
            case "type_text":
                ScreenToolSupport.RejectUnknown(raw, where, "type", "text");
                return new TypeText(ReadText(raw, where));

            case "key":
                ScreenToolSupport.RejectUnknown(raw, where, "type", "key", "modifiers");
                string key = ScreenToolSupport.OptionalString(raw, "key", 32)!;
                if (!DisplayKeys.IsAllowed(key))
                    throw new ScreenToolSupport.ArgumentProblem($"{where}.key is not a known key name.");
                return new KeyStroke(key, ReadModifiers(raw, where));

            case "move":
                ScreenToolSupport.RejectUnknown(raw, where, "type", "x", "y");
                (int mx, int my) = Point(raw, fw, fh, nw, nh);
                return new PointerMove(mx, my);

            case "click":
                ScreenToolSupport.RejectUnknown(raw, where, "type", "x", "y", "button", "count");
                (int cx, int cy) = Point(raw, fw, fh, nw, nh);
                return new PointerClick(cx, cy, ReadButton(raw), ScreenToolSupport.ReadInt(raw, "count", 1, _maxClickCount, fallback: 1));

            case "button_down":
            case "button_up":
                ScreenToolSupport.RejectUnknown(raw, where, "type", "button");
                return new PointerButton(ReadButton(raw), Down: type == "button_down");

            case "scroll":
                ScreenToolSupport.RejectUnknown(raw, where, "type", "x", "y", "delta_x", "delta_y");
                (int sx, int sy) = Point(raw, fw, fh, nw, nh);
                int dx = ScreenToolSupport.ReadInt(raw, "delta_x", -_maxScrollDelta, _maxScrollDelta, fallback: 0);
                int dy = ScreenToolSupport.ReadInt(raw, "delta_y", -_maxScrollDelta, _maxScrollDelta, fallback: 0);
                if (dx == 0 && dy == 0)
                    throw new ScreenToolSupport.ArgumentProblem($"{where} scrolls by nothing.");
                return new PointerScroll(sx, sy, dx, dy);

            default:
                throw new ScreenToolSupport.ArgumentProblem($"{where}.type is not a known action type.");
        }
    }

    private static (int X, int Y) Point(JsonElement raw, int fw, int fh, int nw, int nh)
    {
        int x = ScreenToolSupport.ReadInt(raw, "x", 0, fw - 1);
        int y = ScreenToolSupport.ReadInt(raw, "y", 0, fh - 1);
        return (Scale(x, fw, nw), Scale(y, fh, nh));
    }

    private static PointerButtonKind ReadButton(JsonElement raw) =>
        ScreenToolSupport.OptionalString(raw, "button", 8, required: false) switch
        {
            null or "left" => PointerButtonKind.Left,
            "middle" => PointerButtonKind.Middle,
            "right" => PointerButtonKind.Right,
            _ => throw new ScreenToolSupport.ArgumentProblem("button must be one of left, middle, right."),
        };

    private static KeyModifiers ReadModifiers(JsonElement raw, string where)
    {
        if (!raw.TryGetProperty("modifiers", out JsonElement list))
            return KeyModifiers.None;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 4)
            throw new ScreenToolSupport.ArgumentProblem($"{where}.modifiers must be an array of at most 4 names.");

        KeyModifiers result = KeyModifiers.None;
        foreach (JsonElement m in list.EnumerateArray())
        {
            KeyModifiers one = m.ValueKind == JsonValueKind.String
                ? m.GetString() switch
                {
                    "shift" => KeyModifiers.Shift,
                    "control" => KeyModifiers.Control,
                    "alt" => KeyModifiers.Alt,
                    "meta" => KeyModifiers.Meta,
                    _ => KeyModifiers.None,
                }
                : KeyModifiers.None;
            if (one == KeyModifiers.None)
                throw new ScreenToolSupport.ArgumentProblem($"{where}.modifiers has an unknown modifier.");
            result |= one;
        }

        return result;
    }

    private string ReadText(JsonElement raw, string where)
    {
        string text = ScreenToolSupport.OptionalString(raw, "text", _options.MaxTextLength)!;
        if (text.Length == 0)
            throw new ScreenToolSupport.ArgumentProblem($"{where}.text is empty.");
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c) && c != '\n' && c != '\t')
                throw new ScreenToolSupport.ArgumentProblem($"{where}.text contains a control character.");
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    throw new ScreenToolSupport.ArgumentProblem($"{where}.text is not valid Unicode.");
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new ScreenToolSupport.ArgumentProblem($"{where}.text is not valid Unicode.");
            }
        }

        return text;
    }
}
