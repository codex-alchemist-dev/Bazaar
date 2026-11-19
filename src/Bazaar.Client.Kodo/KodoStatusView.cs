using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Kodo;

/// <summary>
/// The Status view (doc 46 section 14.2, view 1). It draws a <see cref="KodoStatusPanel"/> and holds no logic: every word,
/// colour token and flag comes from <see cref="KodoStatusPaneViewModel"/>. The owner calls <see cref="Refresh"/> on the UI thread
/// when the reducer changes and on a timer (so <c>unobserved</c> appears without an event). Controls route to
/// <see cref="KodoStatusPaneViewModel.RequestControl"/>.
/// </summary>
public sealed class KodoStatusView : UserControl
{
    private readonly KodoStatusPaneViewModel _model;
    private readonly StackPanel _body = new() { Spacing = 8, Margin = new Thickness(16) };

    /// <summary>Builds the view and renders the current panel.</summary>
    public KodoStatusView(KodoStatusPaneViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        Content = _body;
        Focusable = true;
        Refresh();
    }

    /// <summary>Redraws from the view model.</summary>
    public void Refresh()
    {
        var panel = _model.Build();
        _body.Children.Clear();
        KodoViewParts.Name(this, panel.AccessibleSummary);

        if (!panel.Permitted)
        {
            _body.Children.Add(new TextBlock { Text = panel.AccessibleSummary, TextWrapping = TextWrapping.Wrap });
            return;
        }

        var state = new TextBlock { Text = panel.Headline, FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = KodoViewParts.Brush(panel.ColourToken) };
        KodoViewParts.Name(state, $"Kodo state: {panel.Headline}");
        _body.Children.Add(state);

        Add(panel.ScoreText, "Health score");
        foreach (var cap in panel.ScoreCapTexts)
        {
            Add(cap, "Score cap");
        }

        Add(panel.OpenAlertsText, "Open alerts");
        Add(panel.IncidentText, "Open incident");
        Add(panel.ComponentsText, "Components");
        if (panel.IsOffline)
        {
            Add(panel.OfflineText, "Offline age");
        }

        if (panel.Controls.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var control in panel.Controls)
            {
                var button = new Button { Content = control.Label, IsEnabled = control.Enabled, Tag = control.Control };
                KodoViewParts.Name(button, control.AccessibleName);
                if (control.DisabledReason is { } reason)
                {
                    ToolTip.SetTip(button, reason);
                }

                var kind = control.Control;
                button.Click += (_, _) => _model.RequestControl(kind);
                row.Children.Add(button);
            }

            _body.Children.Add(row);
        }
    }

    private void Add(string text, string name)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Foreground = KodoViewParts.Brush(KodoViewParts.SecondaryTextToken);
        KodoViewParts.Name(block, $"{name}: {text}");
        _body.Children.Add(block);
    }
}
