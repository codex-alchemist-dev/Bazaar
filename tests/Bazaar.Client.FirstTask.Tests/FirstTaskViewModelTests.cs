using Bazaar.Contracts.Wire;

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

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task The_wire_sender_posts_one_gc_send_mentioning_the_agent_with_the_token_in_hello()
    {
        var transport = new FakeWireTransport();
        var sender = new WireFirstTaskSender(() => transport, () => Task.FromResult<string?>("tok"), "windows", "test");

        var send = sender.SendAsync(Target, "hi", TestContext.Current.CancellationToken);
        await Until(() => transport.Count > 0 && transport.Latest.SentOfType(WireTypes.GcSend).Any());
        var request = transport.Latest.SentOfType(WireTypes.GcSend).Single();
        var payload = transport.Codec.DecodePayload<GcSend>(request);
        Assert.Equal(Target.ChatId, payload.GcId);
        Assert.Equal([Target.AgentId], payload.Mentions);
        Assert.Equal("hi", payload.Text);
        Assert.Contains("tok", transport.Codec.DecodePayload<Hello>(transport.Latest.SentOfType(WireTypes.Hello).Single()).Credential ?? "");

        transport.Latest.Push(new Envelope(1, WireTypes.GcSend, EnvelopeKind.Response, null, request.Id, null, null,
            DateTimeOffset.UnixEpoch, transport.Codec.EncodePayload(new GcSendResult(7, 1))));
        Assert.Equal(FirstTaskOutcome.Sent, await send);
    }

    [Fact]
    public async Task The_wire_sender_fails_without_a_token_without_connecting()
    {
        var transport = new FakeWireTransport();
        var sender = new WireFirstTaskSender(() => transport, () => Task.FromResult<string?>(null), "windows", "test");

        Assert.Equal(FirstTaskOutcome.Failed, await sender.SendAsync(Target, "hi", TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public void The_endpoint_factory_maps_http_to_ws_and_https_to_wss_without_throwing()
    {
        Assert.NotNull(WireFirstTaskSender.For(new Bazaar.Client.Abstractions.Server.ServerEndpoint(new Uri("http://127.0.0.1:8080/")), () => Task.FromResult<string?>("t"), "windows", "x"));
        Assert.NotNull(WireFirstTaskSender.For(new Bazaar.Client.Abstractions.Server.ServerEndpoint(new Uri("https://example.test/")), () => Task.FromResult<string?>("t"), "windows", "x"));
    }
}
