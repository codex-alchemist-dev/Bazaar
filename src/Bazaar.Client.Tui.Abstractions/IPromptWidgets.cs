namespace Bazaar.Client.Tui;

/// <summary>
/// The widget port (doc 58 work package 2): the few input widgets a wizard needs, with no wizard types in them. The console
/// implementation in <c>Bazaar.Client.Tui</c> runs over <see cref="ITerminal"/>; a Terminal.Gui implementation can replace it without
/// touching the prompter. Every method returns null when the input ended or the operator cancelled.
/// </summary>
public interface IPromptWidgets
{
    /// <summary>A line of text. An empty entry returns <paramref name="defaultValue"/>, or "" when there is none.</summary>
    string? TextEntry(string prompt, string? defaultValue);

    /// <summary>A secret. Nothing typed is echoed, drawn, logged or kept after the call returns (the caller owns the returned text).</summary>
    string? SecretEntry(string prompt);

    /// <summary>One of <paramref name="options"/>, returned as listed. <paramref name="defaultOption"/> is the one an empty entry takes.</summary>
    string? PickOne(string prompt, IReadOnlyList<string> options, string? defaultOption);

    /// <summary>Any number of <paramref name="options"/>, possibly none, in listed order. <paramref name="defaults"/> start selected.</summary>
    IReadOnlyList<string>? PickMany(string prompt, IReadOnlyList<string> options, IReadOnlyList<string> defaults);

    /// <summary>Yes or no. An empty entry takes <paramref name="defaultYes"/>.</summary>
    bool? YesNo(string prompt, bool defaultYes);
}
