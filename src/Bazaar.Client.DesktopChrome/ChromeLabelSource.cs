using Bazaar.Localisation;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// Follows an <see cref="ILocaleSource"/> and keeps the current <see cref="ChromeLabels"/> for it, raising <see cref="Changed"/> after
/// the locale changes so the chrome can rename its controls without a restart.
/// </summary>
public sealed class ChromeLabelSource : IDisposable
{
    private readonly ILocaleSource _locale;
    private readonly List<MessageCatalog> _catalogues;

    public ChromeLabelSource(ILocaleSource locale, IEnumerable<MessageCatalog>? catalogues = null)
    {
        ArgumentNullException.ThrowIfNull(locale);
        _locale = locale;
        _catalogues = catalogues?.ToList() ?? [];
        Labels = ChromeLabels.For(locale.Locale, _catalogues);
        _locale.LocaleChanged += OnLocaleChanged;
    }

    public ChromeLabels Labels { get; private set; }

    /// <summary>Raised after <see cref="Labels"/> was rebuilt for a new locale.</summary>
    public event EventHandler? Changed;

    public void Dispose() => _locale.LocaleChanged -= OnLocaleChanged;

    private void OnLocaleChanged(object? sender, EventArgs e)
    {
        Labels = ChromeLabels.For(_locale.Locale, _catalogues);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
