using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Athanor.Pane;

/// <summary>What the user confirmed in an <see cref="EditorPromptView"/>: the typed text and, for a new document, the language.</summary>
public sealed record EditorPromptResult(string Text, string? LanguageId);

/// <summary>
/// A small themed overlay that asks for a line of text and optionally a language (doc 57 §8). Enter confirms, Escape or the
/// Cancel button cancels. Colours come from dynamic theme resources, so the app theme drives it. <see cref="Result"/>
/// completes once with the answer, or null when cancelled.
/// </summary>
public sealed class EditorPromptView : Border
{
    private readonly TaskCompletionSource<EditorPromptResult?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBox _input;
    private readonly ComboBox? _language;
    private bool _languageChosen;

    /// <param name="title">The prompt title.</param>
    /// <param name="watermark">Hint text in the input.</param>
    /// <param name="languages">Language ids to offer, or null to ask for text only.</param>
    public EditorPromptView(string title, string watermark, IReadOnlyList<string>? languages = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(watermark);

        this.Bind(BackgroundProperty, this.GetResourceObservable("SystemControlBackgroundChromeMediumLowBrush"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseMediumLowBrush"));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(12);
        MinWidth = 360;
        MaxWidth = 560;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 48, 0, 0);
        AutomationProperties.SetName(this, title);

        var heading = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold };
        _input = new TextBox { PlaceholderText = watermark };
        AutomationProperties.SetName(_input, title);
        _input.KeyDown += OnInputKeyDown;

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => Submit();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Cancel();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(heading);
        panel.Children.Add(_input);
        if (languages is { Count: > 0 })
        {
            _language = new ComboBox { ItemsSource = languages, SelectedItem = languages[0], HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(_language, "Language");
            _language.SelectionChanged += (_, _) =>
            {
                if (_language.IsDropDownOpen)
                {
                    _languageChosen = true;
                }
            };
            panel.Children.Add(_language);
            _input.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    GuessLanguage(languages);
                }
            };
        }

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        });
        Child = panel;
        KeyDown += OnKeyDown;
    }

    /// <summary>Completes with the confirmed answer, or null when cancelled.</summary>
    public Task<EditorPromptResult?> Result => _done.Task;

    /// <summary>The text in the input.</summary>
    public string Text
    {
        get => _input.Text ?? string.Empty;
        set => _input.Text = value;
    }

    /// <summary>The language the picker shows, or null when it asks for text only.</summary>
    public string? SelectedLanguage
    {
        get => _language?.SelectedItem as string;
        set
        {
            if (_language is not null)
            {
                _language.SelectedItem = value;
                _languageChosen = true;
            }
        }
    }

    /// <summary>Puts the keyboard focus in the input.</summary>
    public void FocusInput() => _input.Focus();

    /// <summary>Confirms. An empty text counts as cancelled, so the command does nothing.</summary>
    public void Submit()
    {
        var text = Text.Trim();
        _done.TrySetResult(text.Length == 0 ? null : new EditorPromptResult(text, SelectedLanguage));
    }

    /// <summary>Cancels.</summary>
    public void Cancel() => _done.TrySetResult(null);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Submit();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    // Until the user picks a language, follow the one the typed name implies.
    private void GuessLanguage(IReadOnlyList<string> languages)
    {
        if (_languageChosen || _language is null)
        {
            return;
        }

        var guess = LanguageRegistry.Default.Resolve(Text.Trim());
        if (languages.Contains(guess, StringComparer.Ordinal))
        {
            _language.SelectedItem = guess;
        }
    }
}
