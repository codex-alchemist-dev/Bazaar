namespace Bazaar.Client.LocalInference.Guards;

/// <summary>
/// What the output guard found missing from the model output. Each list holds the input items that did not survive.
/// </summary>
public sealed record GuardReport(
    IReadOnlyList<string> DroppedNumbers,
    IReadOnlyList<string> DroppedNames,
    IReadOnlyList<string> DroppedUrls)
{
    /// <summary>True when every number, name and URL from the input survived.</summary>
    public bool Passed => DroppedNumbers.Count == 0 && DroppedNames.Count == 0 && DroppedUrls.Count == 0;
}

/// <summary>Model output after the output cap. <c>Truncated</c> is true when the cap cut the text.</summary>
public sealed record CappedText(string Text, bool Truncated);

/// <summary>
/// The text a caller should show. When <c>FellBack</c> is true the guard rejected the model output and <c>Text</c>
/// is the raw input. <c>Truncated</c> still reports whether the model output was cut to its cap.
/// </summary>
public sealed record GuardedOutput(string Text, bool Truncated, bool FellBack, GuardReport Report);
