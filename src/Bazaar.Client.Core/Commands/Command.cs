namespace Bazaar.Client.Core.Commands;

/// <summary>
/// A command the palette can list and run. Pure data plus an optional enabled predicate;
/// no UI types, so the registry and matcher stay testable without Avalonia.
/// </summary>
public sealed class Command
{
    public Command(
        string id,
        string title,
        string mnemonic,
        IEnumerable<string>? keywords,
        CommandSource source,
        Func<bool>? enabled = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ValidateMnemonic(mnemonic);
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown command source.");
        }

        Id = id;
        Title = title;
        Mnemonic = mnemonic;
        Keywords = (keywords ?? []).Select(ValidateKeyword).ToArray();
        Source = source;
        Enabled = enabled;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>Letters, digits and single inner spaces. Chords run on Enter, so one mnemonic may prefix another.</summary>
    public string Mnemonic { get; }

    public IReadOnlyList<string> Keywords { get; }

    public CommandSource Source { get; }

    /// <summary>Evaluated at query time, never cached. Null means always enabled.</summary>
    public Func<bool>? Enabled { get; }

    /// <summary>Evaluates the enabled predicate now.</summary>
    public bool IsEnabled() => Enabled?.Invoke() ?? true;

    private static void ValidateMnemonic(string mnemonic)
    {
        ArgumentException.ThrowIfNullOrEmpty(mnemonic);
        if (mnemonic[0] == ' ' || mnemonic[^1] == ' ')
        {
            throw new ArgumentException("Mnemonic must not start or end with a space.", nameof(mnemonic));
        }

        foreach (var c in mnemonic)
        {
            if (c != ' ' && !char.IsLetterOrDigit(c))
            {
                throw new ArgumentException(
                    $"Mnemonic '{mnemonic}' contains '{c}'. Only letters, digits and spaces are allowed.",
                    nameof(mnemonic));
            }
        }
    }

    private static string ValidateKeyword(string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        return keyword;
    }
}
