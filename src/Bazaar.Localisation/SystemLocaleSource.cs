using System.Globalization;

namespace Bazaar.Localisation;

/// <summary>
/// An <see cref="ILocaleSource"/> that starts from the operating system's UI language and can be switched at run time
/// (a settings screen calls <see cref="Set"/>). Heads use it until a persisted language setting exists.
/// </summary>
public sealed class SystemLocaleSource : ILocaleSource
{
    private string _locale;

    public SystemLocaleSource()
        : this(CultureInfo.CurrentUICulture.Name)
    {
    }

    public SystemLocaleSource(string locale) => _locale = Normalise(locale);

    public event EventHandler? LocaleChanged;

    public string Locale => _locale;

    /// <summary>Switches the locale and raises <see cref="LocaleChanged"/> when the tag actually changes.</summary>
    public void Set(string locale)
    {
        var next = Normalise(locale);
        if (string.Equals(next, _locale, StringComparison.Ordinal))
        {
            return;
        }

        _locale = next;
        LocaleChanged?.Invoke(this, EventArgs.Empty);
    }

    // The invariant culture has an empty name; "en" is the neutral fallback everywhere else in the platform.
    private static string Normalise(string? locale) => string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
}
