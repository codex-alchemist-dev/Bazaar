namespace Bazaar.Server.CodingEdit;

/// <summary>
/// The edit format an agent uses (doc 17, "Edit formats and benchmark"). Chosen per agent by the setting <c>harness.edit_format</c>.
/// </summary>
public enum EditFormat
{
    /// <summary>The whole new file content is sent. The default until the benchmark measures a model.</summary>
    WholeFile,

    /// <summary>A line range is replaced after the caller proves the current lines with a SHA-256 of them.</summary>
    LineAnchored,

    /// <summary>One search string is replaced, and it must occur exactly once in the file.</summary>
    SearchReplace,
}

/// <summary>The <c>harness.edit_format</c> setting (doc 17, "Settings").</summary>
public static class EditFormatSetting
{
    /// <summary>The settings key for an agent's edit format.</summary>
    public const string Key = "harness.edit_format";

    /// <summary>The shipped default when no value is set.</summary>
    public const EditFormat Default = EditFormat.WholeFile;

    /// <summary>
    /// Maps a setting value (<c>whole-file</c>, <c>line-anchored</c> or <c>search-replace</c>) to a format. Returns false for any
    /// other value, so the caller falls back to <see cref="Default"/>.
    /// </summary>
    public static bool TryParse(string? value, out EditFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "whole-file":
                format = EditFormat.WholeFile;
                return true;
            case "line-anchored":
                format = EditFormat.LineAnchored;
                return true;
            case "search-replace":
                format = EditFormat.SearchReplace;
                return true;
            default:
                format = Default;
                return false;
        }
    }
}
