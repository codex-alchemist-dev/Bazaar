namespace Bazaar.Client.Core.Accessibility;

/// <summary>
/// The accessible name a control exposes to screen readers (UI Automation, AT-SPI, Android accessibility), checked
/// before a control is shown (doc 49 section 2: a CI check fails when a control has no accessible name). Pure: heads
/// call it from their control factories and from tests.
/// </summary>
public static class AccessibleName
{
    /// <summary>Longest name a screen reader reads in one breath before it gets tiresome.</summary>
    public const int MaxLength = 120;

    /// <summary>
    /// Returns null when the name is usable, or a reason it is not. A name must be present, must not be only
    /// whitespace or punctuation, must not be longer than <see cref="MaxLength"/>, and must not be a bare placeholder
    /// such as <c>button</c> or <c>TODO</c>.
    /// </summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "The control has no accessible name.";
        }

        string trimmed = name.Trim();
        if (trimmed.Length > MaxLength)
        {
            return $"The accessible name is longer than {MaxLength} characters.";
        }

        if (!trimmed.Any(char.IsLetterOrDigit))
        {
            return "The accessible name has no letters or digits.";
        }

        if (trimmed is "button" or "TODO" or "untitled" or "icon" or "image")
        {
            return $"The accessible name '{trimmed}' is a placeholder.";
        }

        return null;
    }

    /// <summary>True when <see cref="Problem"/> finds nothing wrong.</summary>
    public static bool IsValid(string? name) => Problem(name) is null;
}
