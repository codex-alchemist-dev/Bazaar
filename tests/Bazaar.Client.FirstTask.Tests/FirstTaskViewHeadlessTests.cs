using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.FirstTask.Avalonia;

namespace Bazaar.Client.FirstTask.Tests;

public class FirstTaskViewHeadlessTests
{
    private sealed class Sender : IFirstTaskSender
    {
        public string? Text { get; private set; }

        public Task<FirstTaskOutcome> SendAsync(FirstTaskTarget target, string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.FromResult(FirstTaskOutcome.Sent);
        }
    }

    private sealed class Targets : IFirstTaskTargetSource
    {
        public Task<FirstTaskTarget?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<FirstTaskTarget?>(new(Guid.NewGuid(), Guid.NewGuid()));
    }

    private static T Find<T>(Control root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    [AvaloniaFact]
    public async Task Typing_and_pressing_Send_posts_the_text_and_locks_the_box()
    {
        var sender = new Sender();
        var model = new FirstTaskViewModel(sender, new Targets());
        var window = new Window { Content = FirstTaskView.Create(model) };
        window.Show();
        await model.StartAsync(TestContext.Current.CancellationToken);

        Find<TextBox>(window, FirstTaskView.Ids.Text).Text = "write a haiku";
        var send = Find<Button>(window, FirstTaskView.Ids.Send);
        Assert.True(send.Command!.CanExecute(null));
        send.Command.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("write a haiku", sender.Text);
        Assert.False(Find<TextBox>(window, FirstTaskView.Ids.Text).IsEnabled);
        Assert.StartsWith("Sent.", Find<TextBlock>(window, FirstTaskView.Ids.Message).Text);
    }
}
