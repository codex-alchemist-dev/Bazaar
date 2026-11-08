using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Core.Vault;

/// <summary>
/// Draws a <see cref="VaultSearchPaneViewModel"/>: a query box, the result cards, and the opened document. Each card
/// is a button; pressing it loads the body. Head-neutral: it uses only Avalonia controls, so Windows and Android share it.
/// </summary>
public sealed class VaultSearchView : UserControl
{
    private readonly VaultSearchPaneViewModel _pane;
    private readonly TextBox _query;
    private readonly TextBlock _status;
    private readonly StackPanel _results;
    private readonly TextBlock _opened;

    public VaultSearchView(VaultSearchPaneViewModel pane)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _pane.Changed += (_, _) => Redraw();

        _query = new TextBox { PlaceholderText = "Search the Vault", Text = pane.Query, MinWidth = 220 };
        _query.Name = "QueryBox";
        _query.TextChanged += (_, _) => _pane.Query = _query.Text ?? string.Empty;

        var search = new Button { Content = "Search", Name = "SearchButton" };
        search.Click += (_, _) => _ = _pane.SearchAsync(CancellationToken.None);
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                _ = _pane.SearchAsync(CancellationToken.None);
                e.Handled = true;
            }
        };

        _status = new TextBlock { Name = "StatusText", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        _results = new StackPanel { Name = "ResultsPanel", Spacing = 6 };
        _opened = new TextBlock { Name = "OpenedBody", TextWrapping = TextWrapping.Wrap, IsVisible = false };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _query, search } },
                _status,
                _results,
                _opened,
            },
        };

        Redraw();
    }

    private void Redraw()
    {
        _status.Text = _pane.StatusMessage;
        _status.IsVisible = _pane.StatusMessage is not null;

        _results.Children.Clear();
        foreach (var card in _pane.Results)
        {
            var title = new TextBlock { Text = card.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            var detail = new TextBlock
            {
                Text = $"{card.Path}  ·  {card.Basis}" + (card.Project is null ? string.Empty : $"  ·  {card.Project}"),
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            };
            var gist = new TextBlock { Text = card.Gist, TextWrapping = TextWrapping.Wrap };
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel { Spacing = 2, Children = { title, detail, gist } },
            };
            button.Click += (_, _) => _ = _pane.OpenAsync(card, CancellationToken.None);
            _results.Children.Add(button);
        }

        var body = _pane.OpenedBody;
        _opened.Text = body;
        _opened.IsVisible = body is not null;
    }
}
