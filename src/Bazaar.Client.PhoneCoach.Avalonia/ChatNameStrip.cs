using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.PhoneCoach.Avalonia;

/// <summary>
/// The top strip of the phone (doc 93): the chat names with the current one highlighted and tappable. The names scroll sideways so the
/// current one is in view and its neighbours peek in from the edges, and a chevron on each side with a neighbour hints that a swipe goes
/// that way. It draws only; the shell feeds it a <see cref="ChatStripState"/> and acts on <see cref="Selected"/>.
/// </summary>
public sealed class ChatNameStrip : Border
{
    private readonly PhoneCoachOptions _options;
    private readonly StackPanel _names = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _previousHint;
    private readonly TextBlock _nextHint;
    private ChatStripState _state = ChatStripState.Empty;

    public ChatNameStrip(PhoneCoachOptions? options = null)
    {
        _options = options ?? PhoneCoachOptions.Default;
        _names.Spacing = _options.StripSpacingDp;
        Height = _options.StripHeightDp;
        Background = ThemeTokens.Brush(PhoneCoachOptions.StripSurfaceToken, Brushes.Transparent);
        _previousHint = Hint("‹", HorizontalAlignment.Left);
        _nextHint = Hint("›", HorizontalAlignment.Right);
        _scroller = new ScrollViewer
        {
            Content = _names,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = new Grid { Children = { _scroller, _previousHint, _nextHint } };
        AutomationProperties.SetName(this, "Chats");
    }

    /// <summary>The state on screen.</summary>
    public ChatStripState State => _state;

    /// <summary>The name buttons in order, so tests and heads can reach them.</summary>
    public IReadOnlyList<Button> NameButtons => _names.Children.OfType<Button>().ToList();

    /// <summary>True when the chevron toward the previous chat is shown.</summary>
    public bool PreviousHintVisible => _previousHint.IsVisible;

    /// <summary>True when the chevron toward the next chat is shown.</summary>
    public bool NextHintVisible => _nextHint.IsVisible;

    /// <summary>Raised with the page index when a name is tapped.</summary>
    public event EventHandler<int>? Selected;

    /// <summary>Shows <paramref name="state"/>: rebuilds the names when they changed, else only moves the highlight.</summary>
    public void Update(ChatStripState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool sameNames = _state.Names.SequenceEqual(state.Names, StringComparer.Ordinal);
        _state = state;
        if (!sameNames)
        {
            Rebuild();
        }

        Highlight();
        _previousHint.IsVisible = state.Previous is not null;
        _nextHint.IsVisible = state.Next is not null;
        AutomationProperties.SetHelpText(this, state.Description);
    }

    private void Rebuild()
    {
        _names.Children.Clear();
        for (int i = 0; i < _state.Names.Count; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = new TextBlock { Text = _state.Names[i], FontSize = _options.StripFontSize, TextTrimming = TextTrimming.CharacterEllipsis },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(_options.StripSpacingDp / 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(button, _state.Names[i]);
            button.Click += (_, _) => Selected?.Invoke(this, index);
            _names.Children.Add(button);
        }
    }

    private void Highlight()
    {
        var text = ThemeTokens.Brush(PhoneCoachOptions.StripTextToken, Brushes.White);
        var muted = ThemeTokens.Brush(PhoneCoachOptions.StripMutedToken, Brushes.Gray);
        var buttons = NameButtons;
        for (int i = 0; i < buttons.Count; i++)
        {
            bool current = i == _state.Selected;
            if (buttons[i].Content is TextBlock label)
            {
                label.FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal;
                label.Foreground = current ? text : muted;
            }

            // The neighbours sit at a lower opacity so the current chat reads first and they read as peeking in.
            buttons[i].Opacity = current ? 1 : _options.MutedOpacity;
            AutomationProperties.SetItemStatus(buttons[i], current ? "Current chat" : "");
        }

        if (_state.Selected >= 0 && _state.Selected < buttons.Count)
        {
            buttons[_state.Selected].BringIntoView();
        }
    }

    private TextBlock Hint(string glyph, HorizontalAlignment side) => new()
    {
        Text = glyph,
        FontSize = _options.StripFontSize,
        Foreground = ThemeTokens.Brush(PhoneCoachOptions.StripMutedToken, Brushes.Gray),
        HorizontalAlignment = side,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
        IsVisible = false,
        // The chevrons are decoration: the position is spoken from the strip's own text.
        [AutomationProperties.AccessibilityViewProperty] = AccessibilityView.Raw,
    };
}
