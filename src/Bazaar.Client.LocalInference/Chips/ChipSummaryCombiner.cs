using Bazaar.Client.LocalInference.Guards;

namespace Bazaar.Client.LocalInference.Chips;

/// <summary>The text on a chip, with the deterministic label always kept.</summary>
/// <param name="Label">The deterministic label. Always present.</param>
/// <param name="Text">What the chip shows: the model line when it passed the guards, otherwise the label.</param>
/// <param name="UsedModelSummary">True when <paramref name="Text"/> is the model line.</param>
public sealed record ChipText(string Label, string Text, bool UsedModelSummary);

/// <summary>
/// Chooses the chip text (doc 31, Design). The model's one-line summary is shown only when it passes the output guards
/// against the input it summarised. Otherwise the chip shows the label. Chips work with no model at all.
/// </summary>
public static class ChipSummaryCombiner
{
    /// <summary>Combines a deterministic label with an optional model line. Only the first non-empty line is used.</summary>
    /// <param name="label">The label from <see cref="ChipLabeler.Label"/>.</param>
    /// <param name="input">The text the model summarised. The guards check the summary against it.</param>
    /// <param name="modelLine">The model's summary, or null when there is none.</param>
    public static ChipText Combine(string label, string input, string? modelLine)
    {
        var line = FirstLine(modelLine);
        if (line is null)
        {
            return new ChipText(label, label, UsedModelSummary: false);
        }

        var guarded = OutputGuard.Apply(LocalTaskKind.ChipSummary, input, line);
        return guarded.FellBack
            ? new ChipText(label, label, UsedModelSummary: false)
            : new ChipText(label, guarded.Text, UsedModelSummary: true);
    }

    private static string? FirstLine(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
    }
}
