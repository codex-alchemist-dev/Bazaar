using System.Globalization;

namespace Bazaar.Client.LocalInference.Chips;

/// <summary>
/// Builds chip labels from action-log entries without a model (doc 31, Design). The same entries always give the same
/// label, for example "Read 15 files; Edited 3 files (.cs, .md); Ran 2 commands". Entries with a count of zero or less are ignored.
/// </summary>
public static class ChipLabeler
{
    /// <summary>File types listed in full in a label. Further types are summarised as "+N more".</summary>
    private const int _maxExtensionsShown = 3;

    /// <summary>Label for entries that are all empty.</summary>
    public const string NoActions = "No actions";

    /// <summary>Returns the deterministic label for <paramref name="entries"/>.</summary>
    public static string Label(IEnumerable<ActionLogEntry> entries)
    {
        var groups = entries
            .Where(e => e.Count > 0)
            .GroupBy(e => (e.Verb, e.Target))
            .OrderBy(g => g.Key.Verb)
            .ThenBy(g => g.Key.Target)
            .Select(g => FormatGroup(g.Key.Verb, g.Key.Target, g.ToList()))
            .ToList();

        return groups.Count == 0 ? NoActions : string.Join("; ", groups);
    }

    private static string FormatGroup(ActionVerb verb, ActionTarget target, IReadOnlyList<ActionLogEntry> entries)
    {
        var total = entries.Sum(e => (long)e.Count);
        var phrase = $"{VerbWord(verb)} {total.ToString(CultureInfo.InvariantCulture)} {Noun(target, total)}";
        if (target != ActionTarget.File)
        {
            return phrase;
        }

        var extensions = ExtensionSummary(entries);
        return extensions.Length == 0 ? phrase : $"{phrase} ({extensions})";
    }

    private static string ExtensionSummary(IReadOnlyList<ActionLogEntry> entries)
    {
        var extensions = entries
            .Where(e => NormaliseExtension(e.FileExtension) is not null)
            .GroupBy(e => NormaliseExtension(e.FileExtension)!, StringComparer.Ordinal)
            .Select(g => (Extension: g.Key, Total: g.Sum(e => (long)e.Count)))
            .OrderByDescending(x => x.Total)
            .ThenBy(x => x.Extension, StringComparer.Ordinal)
            .Select(x => x.Extension)
            .ToList();

        if (extensions.Count == 0)
        {
            return string.Empty;
        }

        var shown = extensions.Take(_maxExtensionsShown).ToList();
        var hidden = extensions.Count - shown.Count;
        var text = string.Join(", ", shown);
        return hidden > 0 ? $"{text}, +{hidden.ToString(CultureInfo.InvariantCulture)} more" : text;
    }

    private static string? NormaliseExtension(string? extension)
    {
        var trimmed = extension?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var withDot = trimmed.StartsWith('.') ? trimmed : "." + trimmed;
        if (withDot.Length == 1 || withDot.Any(char.IsWhiteSpace) || withDot.Contains('/') || withDot.Contains('\\'))
        {
            return null;
        }

        return withDot;
    }

    private static string VerbWord(ActionVerb verb) => verb switch
    {
        ActionVerb.Read => "Read",
        ActionVerb.Edited => "Edited",
        ActionVerb.Created => "Created",
        ActionVerb.Deleted => "Deleted",
        ActionVerb.Ran => "Ran",
        ActionVerb.Fetched => "Fetched",
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
    };

    private static string Noun(ActionTarget target, long count) => (target, count == 1) switch
    {
        (ActionTarget.File, true) => "file",
        (ActionTarget.File, false) => "files",
        (ActionTarget.Command, true) => "command",
        (ActionTarget.Command, false) => "commands",
        (ActionTarget.Web, true) => "page",
        (ActionTarget.Web, false) => "pages",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };
}
