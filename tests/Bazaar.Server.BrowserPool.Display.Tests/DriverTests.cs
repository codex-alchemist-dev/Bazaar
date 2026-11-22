using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.BrowserPool.Display.Tests;

public sealed class DriverTests : IAsyncDisposable
{
    private const string _session = "0123456789abcdef0123456789abcdef";

    private readonly FakeBrowser _browser = new();
    private readonly FakePipes _pipes = new();
    private readonly PoolBrowserPageDriver _driver;

    public DriverTests()
    {
        _pipes.Add(_session, _browser.Pipe);
        _driver = new PoolBrowserPageDriver(_pipes, new BrowserPageDriverOptions { CommandTimeout = TimeSpan.FromSeconds(5) });
    }

    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync();
        await _browser.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DisplayTarget Target(string? browserSession) => new("agent-a", Guid.NewGuid(), DisplayGuestOs.Linux, "backend", browserSession);

    [Fact]
    public async Task The_first_call_finds_the_page_attaches_flat_and_then_sends_the_page_command_with_the_target_session()
    {
        var png = await _driver.CaptureScreenshotPngAsync(_session, Ct);

        Assert.Equal(FakeBrowser.TinyPng, png);
        var sent = _browser.Received;
        Assert.Equal(["Target.getTargets", "Target.attachToTarget", "Page.captureScreenshot"], sent.Select(c => c.Method));
        Assert.Null(sent[0].SessionId);
        Assert.Equal("PAGE-1", sent[1].Params.GetProperty("targetId").GetString());
        Assert.True(sent[1].Params.GetProperty("flatten").GetBoolean());
        Assert.Null(sent[1].SessionId);
        Assert.Equal("SESSION-1", sent[2].SessionId);
        Assert.Equal("png", sent[2].Params.GetProperty("format").GetString());
        Assert.False(sent[2].Params.GetProperty("captureBeyondViewport").GetBoolean());
    }

    [Fact]
    public async Task The_attachment_is_kept_for_later_calls()
    {
        await _driver.CaptureScreenshotPngAsync(_session, Ct);
        await _driver.CaptureScreenshotPngAsync(_session, Ct);
        await _driver.DispatchInputAsync(_session, [new PointerMove(1, 2)], Ct);

        Assert.Single(_browser.Received, c => c.Method == "Target.getTargets");
        Assert.Single(_browser.Received, c => c.Method == "Target.attachToTarget");
    }

    [Fact]
    public async Task The_page_with_the_lowest_target_id_is_chosen_whatever_order_the_browser_lists_them_in()
    {
        _browser.Targets.Clear();
        _browser.Targets.Add("{\"targetId\":\"0-WORKER\",\"type\":\"service_worker\",\"url\":\"\"}");
        _browser.Targets.Add("{\"targetId\":\"B-PAGE\",\"type\":\"page\",\"url\":\"about:blank\"}");
        _browser.Targets.Add("{\"targetId\":\"A-PAGE\",\"type\":\"page\",\"url\":\"about:blank\"}");
        _browser.Targets.Add("{\"targetId\":\"C-PAGE\",\"type\":\"page\",\"url\":\"about:blank\"}");

        await _driver.CaptureScreenshotPngAsync(_session, Ct);

        Assert.Equal("A-PAGE", _browser.Received.Single(c => c.Method == "Target.attachToTarget").Params.GetProperty("targetId").GetString());
    }

    [Fact]
    public async Task A_browser_with_no_page_gives_a_no_page_error()
    {
        _browser.Targets.Clear();
        _browser.Targets.Add("{\"targetId\":\"W\",\"type\":\"service_worker\",\"url\":\"\"}");

        var e = await Assert.ThrowsAsync<PageDriverException>(() => _driver.CaptureScreenshotPngAsync(_session, Ct));

        Assert.Equal(PageDriverFailure.NoPage, e.Failure);
    }

    [Fact]
    public async Task When_the_target_session_has_gone_the_driver_attaches_again_and_retries_once()
    {
        await _driver.CaptureScreenshotPngAsync(_session, Ct);
        _browser.NewSession();

        var png = await _driver.CaptureScreenshotPngAsync(_session, Ct);

        Assert.Equal(FakeBrowser.TinyPng, png);
        Assert.Equal(2, _browser.Received.Count(c => c.Method == "Target.attachToTarget"));
        Assert.Equal(3, _browser.Received.Count(c => c.Method == "Page.captureScreenshot"));
    }

    [Fact]
    public async Task A_session_that_keeps_failing_is_retried_only_once()
    {
        _browser.Handler = c => Task.FromResult(c.Method switch
        {
            "Target.getTargets" => FakeReply.Result("{\"targetInfos\":[{\"targetId\":\"P\",\"type\":\"page\"}]}"),
            "Target.attachToTarget" => FakeReply.Result("{\"sessionId\":\"S\"}"),
            _ => FakeReply.Error(-32001),
        });

        var e = await Assert.ThrowsAsync<PageDriverException>(() => _driver.CaptureScreenshotPngAsync(_session, Ct));

        Assert.Equal(-32001, e.ProtocolCode);
        Assert.Equal(2, _browser.Received.Count(c => c.Method == "Page.captureScreenshot"));
    }

    [Fact]
    public async Task A_screenshot_over_the_limit_is_refused_on_the_base64_length_before_any_decoding()
    {
        await using var browser = new FakeBrowser();
        var pipes = new FakePipes();
        pipes.Add("big", browser.Pipe);
        await using var driver = new PoolBrowserPageDriver(pipes, new BrowserPageDriverOptions { MaxScreenshotBytes = 1024 });
        // Not valid base64: if the driver decoded first, the failure would be BadResponse, not TooLarge.
        browser.ScreenshotData = new string('!', 4000);

        var e = await Assert.ThrowsAsync<PageDriverException>(() => driver.CaptureScreenshotPngAsync("big", Ct));

        Assert.Equal(PageDriverFailure.TooLarge, e.Failure);
    }

    [Fact]
    public async Task A_screenshot_exactly_at_the_limit_is_accepted()
    {
        await using var browser = new FakeBrowser();
        var pipes = new FakePipes();
        pipes.Add("fit", browser.Pipe);
        await using var driver = new PoolBrowserPageDriver(pipes, new BrowserPageDriverOptions { MaxScreenshotBytes = FakeBrowser.TinyPng.Length });

        Assert.Equal(FakeBrowser.TinyPng, await driver.CaptureScreenshotPngAsync("fit", Ct));
    }

    [Fact]
    public async Task A_reply_that_is_not_a_png_or_not_base64_is_a_bad_response()
    {
        _browser.Png = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        var e1 = await Assert.ThrowsAsync<PageDriverException>(() => _driver.CaptureScreenshotPngAsync(_session, Ct));
        Assert.Equal(PageDriverFailure.BadResponse, e1.Failure);

        _browser.ScreenshotData = "!!!!";
        var e2 = await Assert.ThrowsAsync<PageDriverException>(() => _driver.CaptureScreenshotPngAsync(_session, Ct));
        Assert.Equal(PageDriverFailure.BadResponse, e2.Failure);
    }

    [Fact]
    public async Task The_png_bytes_come_back_untouched()
    {
        var bytes = new byte[5000];
        Random.Shared.NextBytes(bytes);
        FakeBrowser.TinyPng.AsSpan(0, 8).CopyTo(bytes);
        _browser.Png = bytes;

        Assert.Equal(bytes, await _driver.CaptureScreenshotPngAsync(_session, Ct));
    }

    [Fact]
    public async Task Supports_is_true_only_for_a_browser_session_the_pool_has_a_pipe_for()
    {
        Assert.True(_driver.Supports(Target(_session)));
        Assert.False(_driver.Supports(Target(null)));
        Assert.False(_driver.Supports(Target(string.Empty)));
        Assert.False(_driver.Supports(Target("unknown-session")));
        Assert.False(_driver.Supports(Target(new string('a', 5000))));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_session_is_retired_when_the_pool_stops_returning_its_pipe()
    {
        await _driver.CaptureScreenshotPngAsync(_session, Ct);
        Assert.Equal(1, _driver.OpenSessionCount);

        _pipes.Remove(_session);

        Assert.False(_driver.Supports(Target(_session)));
        Assert.Equal(0, _driver.OpenSessionCount);
        var e = await Assert.ThrowsAsync<PageDriverException>(() => _driver.CaptureScreenshotPngAsync(_session, Ct));
        Assert.Equal(PageDriverFailure.NoPipe, e.Failure);
    }

    [Fact]
    public async Task A_new_pipe_under_the_same_id_gets_a_new_session()
    {
        await _driver.CaptureScreenshotPngAsync(_session, Ct);
        await using var second = new FakeBrowser();
        _pipes.Add(_session, second.Pipe);

        await _driver.CaptureScreenshotPngAsync(_session, Ct);

        Assert.Equal(3, second.Received.Count);
        Assert.Equal(1, _driver.OpenSessionCount);
    }

    [Fact]
    public async Task Sessions_are_made_lazily_one_per_id()
    {
        await using var other = new FakeBrowser();
        _pipes.Add("other", other.Pipe);
        Assert.Equal(0, _driver.OpenSessionCount);

        await _driver.CaptureScreenshotPngAsync(_session, Ct);
        await _driver.CaptureScreenshotPngAsync("other", Ct);
        await _driver.CaptureScreenshotPngAsync("other", Ct);

        Assert.Equal(2, _driver.OpenSessionCount);
    }

    [Fact]
    public async Task A_pipe_that_ended_fails_calls_with_pipe_closed()
    {
        await _driver.CaptureScreenshotPngAsync(_session, Ct);
        _browser.Close();
        await DevToolsSessionTests.WaitUntilAsync(() => _driver.OpenSessionCount == 1);
        await Task.Delay(100, Ct);

        var e = await Assert.ThrowsAsync<PageDriverException>(() => _driver.CaptureScreenshotPngAsync(_session, Ct));

        Assert.Equal(PageDriverFailure.PipeClosed, e.Failure);
    }

    [Fact]
    public async Task Input_batches_for_one_session_do_not_interleave()
    {
        var inFlight = 0;
        var overlapped = false;
        _browser.Handler = async c =>
        {
            if (c.Method == "Input.dispatchMouseEvent")
            {
                if (Interlocked.Increment(ref inFlight) > 1)
                {
                    overlapped = true;
                }

                await Task.Delay(5);
                Interlocked.Decrement(ref inFlight);
                return FakeReply.Result("{}");
            }

            return c.Method == "Target.getTargets"
                ? FakeReply.Result("{\"targetInfos\":[{\"targetId\":\"P\",\"type\":\"page\"}]}")
                : FakeReply.Result("{\"sessionId\":\"S\"}");
        };

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => _driver.DispatchInputAsync(_session, [new PointerClick(i, i)], Ct)));

        Assert.False(overlapped);
        Assert.Equal(6 * 3, _browser.Input.Count);
    }

    [Fact]
    public async Task A_batch_with_an_invalid_action_sends_nothing()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _driver.DispatchInputAsync(_session, [new PointerClick(5, 5), new KeyStroke("NoSuchKey")], Ct));

        Assert.Empty(_browser.Received);
    }

    [Fact]
    public async Task A_pointer_button_acts_at_the_position_the_last_batch_left_the_pointer()
    {
        await _driver.DispatchInputAsync(_session, [new PointerMove(40, 50)], Ct);
        await _driver.DispatchInputAsync(_session, [new PointerButton(PointerButtonKind.Left, true), new PointerButton(PointerButtonKind.Left, false)], Ct);

        var pressed = _browser.Input[1].Params;
        Assert.Equal("mousePressed", pressed.GetProperty("type").GetString());
        Assert.Equal(40, pressed.GetProperty("x").GetInt32());
        Assert.Equal(50, pressed.GetProperty("y").GetInt32());
        Assert.Equal(1, pressed.GetProperty("buttons").GetInt32());
        Assert.Equal(0, _browser.Input[2].Params.GetProperty("buttons").GetInt32());
    }

    [Fact]
    public async Task An_empty_or_oversized_batch_and_an_empty_session_id_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _driver.DispatchInputAsync(_session, [], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _driver.DispatchInputAsync(_session, [.. Enumerable.Repeat<InputAction>(new PointerMove(1, 1), 201)], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _driver.DispatchInputAsync(string.Empty, [new PointerMove(1, 1)], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _driver.CaptureScreenshotPngAsync(string.Empty, Ct));
    }

    [Fact]
    public async Task Disposing_the_driver_closes_its_sessions()
    {
        await _driver.CaptureScreenshotPngAsync(_session, Ct);

        await _driver.DisposeAsync();

        Assert.Equal(0, _driver.OpenSessionCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _driver.CaptureScreenshotPngAsync(_session, Ct));
    }
}
