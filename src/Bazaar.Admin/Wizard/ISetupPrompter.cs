namespace Bazaar.Admin;

/// <summary>
/// The wizard's conversation with the operator: lines out, one answer in per question. The console version is
/// <see cref="ConsoleSetupPrompter"/>; tests supply a scripted one. Secret values never go through this interface (see
/// <see cref="ISecretValueSource"/>).
/// </summary>
public interface ISetupPrompter
{
    /// <summary>Writes one line of text to the operator.</summary>
    void Say(string line);

    /// <summary>
    /// Asks one question. Returns the typed answer without its line ending. An empty answer is returned as "" when there is no
    /// default, and as <paramref name="defaultValue"/> when there is one. Returns null when the input has ended, which stops the wizard.
    /// </summary>
    string? Ask(string question, string? defaultValue);
}
