namespace Bazaar.Localisation;

/// <summary>The viewer's active locale and a signal when it changes, so a head can react without a restart.</summary>
public interface ILocaleSource
{
    /// <summary>The active BCP 47 tag, for example "en", "ar-EG".</summary>
    string Locale { get; }

    /// <summary>Raised after <see cref="Locale"/> changes.</summary>
    event EventHandler? LocaleChanged;
}
