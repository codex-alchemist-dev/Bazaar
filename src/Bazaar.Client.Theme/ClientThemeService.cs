namespace Bazaar.Client.Theme;

/// <summary>
/// The client's current validated theme and a notification when it changes (doc 41 section 3, doc 57 section 2.2). A head owns
/// one. Consumers (the editor sync, later the shell) read <see cref="Current"/> and listen to <see cref="Changed"/>. No Avalonia
/// types. <see cref="Set"/> may be called from any thread; <see cref="Changed"/> is raised on the caller's thread.
/// </summary>
public sealed class ClientThemeService
{
    public const string BuiltInDarkResource = "Bazaar.Client.Theme.BuiltIn.dark.theme.json";

    private ThemeDefinition _current;

    public ClientThemeService(ThemeDefinition initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>The theme in force. Never null.</summary>
    public ThemeDefinition Current => Volatile.Read(ref _current);

    /// <summary>Raised after <see cref="Current"/> changed to a different theme instance.</summary>
    public event EventHandler? Changed;

    /// <summary>The theme that ships inside this assembly. Always valid.</summary>
    public static ThemeDefinition BuiltInDark()
    {
        using Stream stream = typeof(ClientThemeService).Assembly.GetManifestResourceStream(BuiltInDarkResource)
            ?? throw new InvalidOperationException($"The built-in theme resource '{BuiltInDarkResource}' is missing.");
        using var reader = new StreamReader(stream);
        ThemeLoadResult result = ThemeValidator.Validate(reader.ReadToEnd());
        return result.Definition ?? throw new InvalidOperationException("The built-in theme does not validate.");
    }

    /// <summary>
    /// Starts from the theme file when it exists and validates, else from the built-in dark theme. A missing file is normal and
    /// silent; an unreadable or invalid one is reported through <paramref name="log"/> and the built-in theme is used.
    /// </summary>
    public static ClientThemeService Create(string? themeFile, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (themeFile is not null && File.Exists(themeFile))
        {
            try
            {
                ThemeLoadResult result = ThemeValidator.Validate(File.ReadAllText(themeFile));
                if (result.Definition is { } theme)
                {
                    return new ClientThemeService(theme);
                }

                log($"Theme: '{themeFile}' was not used: {string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code}"))}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Theme: '{themeFile}' could not be read ({ex.Message}); using the built-in theme.");
            }
        }

        return new ClientThemeService(BuiltInDark());
    }

    /// <summary>Makes <paramref name="theme"/> current and raises <see cref="Changed"/>. The same instance is ignored.</summary>
    public void Set(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (ReferenceEquals(Interlocked.Exchange(ref _current, theme), theme))
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
