using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.FirstTask.Avalonia;

/// <summary>
/// The first-task composer's body. Controls carry stable automation ids (see <see cref="Ids"/>). Nothing is focused when it opens
/// (doc 27); Tab reaches each control. Enter adds a line; Send is a button (no chords).
/// </summary>
public static class FirstTaskView
{
    public static class Ids
    {
        public const string Message = "firsttask-message";
        public const string Text = "firsttask-text";
        public const string Send = "firsttask-send";
    }

    public static StackPanel Create(FirstTaskViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(message, Ids.Message);
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);

        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxLength = FirstTaskViewModel.MaxLength, PlaceholderText = "Your first task" };
        AutomationProperties.SetAutomationId(text, Ids.Text);
        AutomationProperties.SetName(text, "First task");
        text.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && model.Text != (text.Text ?? "")) model.Text = text.Text ?? "";
        };

        var send = new Button { Content = "Send", Command = model.SendCommand, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(send, Ids.Send);

        void Refresh()
        {
            message.Text = model.Message;
            text.IsEnabled = !model.Sent;
        }

        model.PropertyChanged += (_, _) => Refresh();
        Refresh();

        return new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = "Give a first task", FontWeight = FontWeight.SemiBold, FontSize = 20 }, message, text, send },
        };
    }
}
