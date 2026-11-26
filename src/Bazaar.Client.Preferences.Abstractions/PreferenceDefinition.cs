namespace Bazaar.Client.Preferences.Abstractions;

/// <summary>The value type of a preference.</summary>
public enum PreferenceKind
{
    Bool,
    WholeNumber,
    Text,
    Choice,
}

/// <summary>
/// One client preference as the settings registry declares it (doc 27 section on preferences, doc 03). The client reads
/// defaults and limits from here and hard-codes none. Values are stored as canonical strings: <c>true</c>/<c>false</c> for
/// <see cref="PreferenceKind.Bool"/>, invariant decimal for <see cref="PreferenceKind.WholeNumber"/>.
/// </summary>
/// <param name="Key">Dotted key, unique across modules.</param>
/// <param name="Kind">The value type.</param>
/// <param name="Default">The default, already in canonical form.</param>
/// <param name="Choices">The allowed values for <see cref="PreferenceKind.Choice"/>.</param>
/// <param name="Min">Lower bound for <see cref="PreferenceKind.WholeNumber"/>, inclusive.</param>
/// <param name="Max">Upper bound for <see cref="PreferenceKind.WholeNumber"/>, inclusive; also the length cap for <see cref="PreferenceKind.Text"/>.</param>
public sealed record PreferenceDefinition(
    string Key,
    PreferenceKind Kind,
    string Default,
    IReadOnlyList<string>? Choices = null,
    long? Min = null,
    long? Max = null);

/// <summary>Implemented by a module that declares preferences. The head collects the declarations when it builds the store.</summary>
public interface IPreferenceDeclarer
{
    IEnumerable<PreferenceDefinition> Preferences { get; }
}
