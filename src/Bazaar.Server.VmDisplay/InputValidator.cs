using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay;

/// <summary>Validates targets and input batches before anything reaches a gate or a source.</summary>
internal static class InputValidator
{
    private const int MaxScrollDelta = 10_000;
    private const int MaxClickCount = 3;
    private const KeyModifiers AllModifiers = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;

    /// <summary>Throws unless the target names an agent and a VM.</summary>
    public static void ValidateTarget(DisplayTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.AgentId))
            throw Invalid("The target has no agent id.");
        if (target.VmId == Guid.Empty)
            throw Invalid("The target has no VM id.");
        if (!Enum.IsDefined(target.Os))
            throw Invalid("The target has an unknown guest OS.");
    }

    /// <summary>Throws unless every action in the batch is valid and the batch is within the limits.</summary>
    public static void ValidateBatch(IReadOnlyList<InputAction>? actions, VmDisplayOptions options)
    {
        if (actions is null || actions.Count == 0)
            throw Invalid("The input batch is empty.");
        if (actions.Count > options.MaxActionsPerBatch)
            throw Invalid($"The input batch has {actions.Count} actions, above the limit of {options.MaxActionsPerBatch}.");
        foreach (InputAction action in actions)
            ValidateAction(action, options);
    }

    private static void ValidateAction(InputAction? action, VmDisplayOptions options)
    {
        switch (action)
        {
            case PointerMove m:
                Point(m.X, m.Y);
                break;
            case PointerButton b:
                if (!Enum.IsDefined(b.Button))
                    throw Invalid("Unknown pointer button.");
                break;
            case PointerClick c:
                Point(c.X, c.Y);
                if (!Enum.IsDefined(c.Button))
                    throw Invalid("Unknown pointer button.");
                if (c.Count < 1 || c.Count > MaxClickCount)
                    throw Invalid($"A click count must be 1 to {MaxClickCount}.");
                break;
            case PointerScroll s:
                Point(s.X, s.Y);
                if (Math.Abs((long)s.DeltaX) > MaxScrollDelta || Math.Abs((long)s.DeltaY) > MaxScrollDelta)
                    throw Invalid($"A scroll delta must be within {MaxScrollDelta}.");
                break;
            case KeyStroke k:
                if (!DisplayKeys.IsAllowed(k.Key))
                    throw Invalid("The key is not on the allowlist.");
                if ((k.Modifiers & ~AllModifiers) != 0)
                    throw Invalid("Unknown key modifier.");
                break;
            case TypeText t:
                ValidateText(t.Text, options.MaxTextLength);
                break;
            default:
                throw Invalid("Unknown input action.");
        }
    }

    private static void Point(int x, int y)
    {
        if (x < 0 || y < 0 || x >= DisplayLimits.CeilingWidth || y >= DisplayLimits.CeilingHeight)
            throw Invalid("Pointer coordinates must be non-negative and within the display ceiling.");
    }

    private static void ValidateText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            throw Invalid("Text to type is empty.");
        if (text.Length > maxLength)
            throw Invalid($"Text to type is {text.Length} characters, above the limit of {maxLength}.");
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c) && c != '\n' && c != '\t')
                throw Invalid("Text to type contains a control character.");
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    throw Invalid("Text to type is not valid Unicode.");
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw Invalid("Text to type is not valid Unicode.");
            }
        }
    }

    private static DisplayException Invalid(string why) => new(DisplayFailure.InvalidInput, why);
}
