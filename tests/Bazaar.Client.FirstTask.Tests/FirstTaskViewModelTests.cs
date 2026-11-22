using Bazaar.Client.Components.Chat;
using Bazaar.Contracts.Wire;
using System.Threading.Channels;

namespace Bazaar.Client.FirstTask.Tests;

public class FirstTaskViewModelTests
{
    private static readonly FirstTaskTarget Target = new(Guid.NewGuid(), Guid.NewGuid());

    private sealed class Sender(FirstTaskOutcome outcome) : IFirstTaskSender
    {
        public List<(FirstTaskTarget Target, string Text)> Sent { get; } = [];

        public Task<FirstTaskOutcome> SendAsync(FirstTaskTarget target, string text, CancellationToken cancellationToken = default)
        {
            Sent.Add((target, text));
            return Task.FromResult(outcome);
        }
    }

    private sealed class Targets(FirstTaskTarget? target, bool throws = false) : IFirstTaskTargetSource
    {
        public Task<FirstTaskTarget?> ReadAsync(CancellationToken cancellationToken = default) =>
            throws ? throw new HttpRequestException("down") : Task.FromResult(target);
    }

    private static async Task Press(System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        await Task.Delay(30, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Send_is_off_until_a_target_and_text_exist()
    {
        var sender = new Sender(FirstTaskOutcome.Sent);
        var none = new FirstTaskViewModel(sender, new Targets(null));
        await none.StartAsync(TestContext.Current.CancellationToken);
        none.Text = "hello";
        Assert.False(none.SendCommand.CanExecute(null));
        Assert.Contains("not ready", none.Message);

        var ready = new FirstTaskViewModel(sender, new Targets(Target));
        await ready.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(ready.SendCommand.CanExecute(null)); // no text yet
        ready.Text = "   ";
        Assert.False(ready.SendCommand.CanExecute(null));
        ready.Text = "  summarise the repo  ";
        Assert.True(ready.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Send_posts_the_trimmed_text_once_and_locks_the_box()
    {
        var sender = new Sender(FirstTaskOutcome.Sent);
        var model = new FirstTaskViewModel(sender, new Targets(Target));
        await model.StartAsync(TestContext.Current.CancellationToken);
        model.Text = "  summarise the repo  ";

        await Press(model.SendCommand);

        Assert.Equal([(Target, "summarise the repo")], sender.Sent);
        Assert.True(model.Sent);
        Assert.False(model.SendCommand.CanExecute(null));
        Assert.DoesNotContain("summarise", model.Message);
    }

    [Fact]
    public async Task A_failed_send_keeps_the_text_and_allows_a_retry()
    {
        var model = new FirstTaskViewModel(new Sender(FirstTaskOutcome.Failed), new Targets(Target));
        await model.StartAsync(TestContext.Current.CancellationToken);
        model.Text = "do a thing";

        await Press(model.SendCommand);

        Assert.False(model.Sent);
        Assert.Equal("do a thing", model.Text);
        Assert.True(model.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_unreachable_server_reads_as_not_ready_and_long_text_is_capped()
    {
        var model = new FirstTaskViewModel(new Sender(FirstTaskOutcome.Sent), new Targets(Target, throws: true));
        await model.StartAsync(TestContext.Current.CancellationToken);
        Assert.Contains("not ready", model.Message);

        model.Text = new string('x', FirstTaskViewModel.MaxLength + 50);
        Assert.Equal(FirstTaskViewModel.MaxLength, model.Text.Length);
    }

    private sealed class FakeWire(bool fail) : IGcWire
    {
        public GcSend? Last { get; private set; }

        public ChannelReader<WireGcMessage> Messages => Channel.CreateUnbounded<WireGcMessage>().Reader;

        public Task SubscribeAsync(string stream, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<GcSendResult> SendAsync(GcSend request, CancellationToken cancellationToken)
        {
            Last = request;
            return fail ? throw new IOException("down") : Task.FromResult(new GcSendResult(1, 1));
        }

        public Task<GcPage> HistoryAsync(GcHistory request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_wire_sender_posts_one_gc_send_mentioning_the_agent_and_reports_failures()
    {
        var wire = new FakeWire(fail: false);
        Assert.Equal(FirstTaskOutcome.Sent, await new GcFirstTaskSender(wire).SendAsync(Target, "hi", TestContext.Current.CancellationToken));
        Assert.Equal(Target.ChatId, wire.Last!.GcId);
        Assert.Equal([Target.AgentId], wire.Last.Mentions);
        Assert.Equal("hi", wire.Last.Text);

        Assert.Equal(FirstTaskOutcome.Failed, await new GcFirstTaskSender(new FakeWire(fail: true)).SendAsync(Target, "hi", TestContext.Current.CancellationToken));
    }
}
