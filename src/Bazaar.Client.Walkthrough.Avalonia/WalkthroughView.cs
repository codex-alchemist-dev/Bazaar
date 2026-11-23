using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Walkthrough.Avalonia;

/// <summary>
/// The walkthrough card. Controls carry stable automation ids (see <see cref="Ids"/>) for tests and the smoke harness. Nothing is
/// focused when it opens (doc 27); there is no animation, so reduced motion needs no substitute. Colours come from the theme's
/// resources (doc 93), so a Rubedo override restyles the card without a code change.
/// </summary>
public static class WalkthroughView
{
    public static class Ids
    {
        public const string Card = "walkthrough-card";
        public const string Progress = "walkthrough-progress";
        public const string Title = "walkthrough-title";
        public const string Body = "walkthrough-body";
        public const string Done = "walkthrough-step-done";
        public const string Action = "walkthrough-action";
        public const string Back = "walkthrough-back";
        public const string Next = "walkthrough-next";
        public const string Skip = "walkthrough-skip";
        public const string Refresh = "walkthrough-refresh";
        public const string Status = "walkthrough-status";
    }

    public static Border Create(WalkthroughViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var progress = new TextBlock { Opacity = 0.7 };
        Id(progress, Ids.Progress);
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 20, TextWrapping = TextWrapping.Wrap };
        Id(title, Ids.Title);
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Id(body, Ids.Body);
        var done = new TextBlock { Text = "Done" };
        Id(done, Ids.Done);
        var status = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        Id(status, Ids.Status);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var action = Button("", Ids.Action, model.ActionCommand);
        var back = Button("Back", Ids.Back, model.BackCommand);
        var next = Button("Next", Ids.Next, model.NextCommand);
        var skip = Button("Skip walkthrough", Ids.Skip, model.SkipCommand);
        var refresh = Button("Check progress", Ids.Refresh, model.RefreshCommand);

        var card = new Border
        {
            Padding = new global::Avalonia.Thickness(16),
            CornerRadius = new global::Avalonia.CornerRadius(8),
            BorderThickness = new global::Avalonia.Thickness(1),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    progress, title, body, done, action, status,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { back, next, refresh, skip } },
                },
            },
        };
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Bazaar.brush.border"));
        Id(card, Ids.Card);
        AutomationProperties.SetName(card, "First-run walkthrough");

        void Refresh()
        {
            card.IsVisible = model.Visible;
            progress.Text = model.ProgressText;
            title.Text = model.Title;
            body.Text = model.Body;
            done.IsVisible = model.IsStepDone;
            action.IsVisible = model.ActionLabel is not null;
            action.Content = model.ActionLabel;
            status.Text = model.Status;
            status.IsVisible = model.Status.Length > 0;
        }

        model.PropertyChanged += (_, _) => Refresh();
        Refresh();
        return card;
    }

    private static Button Button(string text, string id, System.Windows.Input.ICommand command)
    {
        var button = new Button { Content = text, Command = command, HorizontalAlignment = HorizontalAlignment.Left };
        Id(button, id);
        return button;
    }

    private static void Id(Control control, string id) => AutomationProperties.SetAutomationId(control, id);
}
