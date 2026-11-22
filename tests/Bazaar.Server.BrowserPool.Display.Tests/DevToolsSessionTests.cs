using System.Text;
using System.Text.Json;

namespace Bazaar.Server.BrowserPool.Display.Tests;

public sealed class DevToolsSessionTests
{
    private static readonly byte[] _emptyParams = "{}"u8.ToArray();

    private static BrowserPageDriverOptions Fast(Action<BrowserPageDriverOptions>? tweak = null)
    {
        var o = new BrowserPageDriverOptions { CommandTimeout = TimeSpan.FromSeconds(5) };
        tweak?.Invoke(o);
        return o;
    }

    private static byte[] Params(int n) => Encoding.UTF8.GetBytes("{\"n\":" + n + "}");

    [Fact]
    public async Task A_reply_goes_to_the_call_that_asked_and_the_request_has_the_expected_shape()
    {
        await using var browser = new FakeBrowser();
        await using var session = new DevToolsSession(browser.Pipe, Fast());

        var reply = await session.SendAsync(CdpMethod.TargetGetTargets, null, _emptyParams, TestContext.Current.CancellationToken);

        Assert.Contains("targetInfos", Encoding.UTF8.GetString(reply), StringComparison.Ordinal);
        var sent = Assert.Single(browser.Received);
        Assert.Equal("Target.getTargets", sent.Method);
        Assert.Null(sent.SessionId);
        Assert.Equal(0, session.PendingCount);
    }

    [Fact]
    public async Task Concurrent_calls_keep_their_ids_apart_even_when_replies_arrive_out_of_order()
    {
        await using var browser = new FakeBrowser();
        // Echo n back, with the earliest requests answered last.
        browser.Handler = async c =>
        {
            var n = c.Params.GetProperty("n").GetInt32();
            await Task.Delay((20 - n) * 10);
            return FakeReply.Result("{\"n\":" + n + "}");
        };
        await using var session = new DevToolsSession(browser.Pipe, Fast(o => o.MaxPendingRequests = 64));

        var calls = Enumerable.Range(0, 20)
            .Select(n => session.SendAsync(CdpMethod.InputInsertText, "S", Params(n), TestContext.Current.CancellationToken))
            .ToArray();
        var replies = await Task.WhenAll(calls);

        for (var n = 0; n < 20; n++)
        {
            using var doc = JsonDocument.Parse(replies[n]);
            Assert.Equal(n, doc.RootElement.GetProperty("result").GetProperty("n").GetInt32());
        }

        Assert.Equal(20, browser.Received.Select(c => c.Id).Distinct().Count());
        Assert.Equal(0, session.PendingCount);
    }

    [Fact]
    public async Task A_call_with_no_reply_times_out_and_leaves_the_session_usable()
    {
        await using var browser = new FakeBrowser();
        browser.Handler = c => Task.FromResult(c.Params.GetProperty("n").GetInt32() == 0 ? FakeReply.None : FakeReply.Result("{}"));
        await using var session = new DevToolsSession(browser.Pipe, Fast(o => o.CommandTimeout = TimeSpan.FromMilliseconds(200)));

        await Assert.ThrowsAsync<TimeoutException>(() => session.SendAsync(CdpMethod.InputInsertText, "S", Params(0), TestContext.Current.CancellationToken));
        Assert.Equal(0, session.PendingCount);
        Assert.False(session.IsClosed);

        await session.SendAsync(CdpMethod.InputInsertText, "S", Params(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_late_reply_to_a_timed_out_call_is_dropped()
    {
        await using var browser = new FakeBrowser();
        var release = new TaskCompletionSource();
        browser.Handler = async c =>
        {
            if (c.Params.GetProperty("n").GetInt32() == 0)
            {
                await release.Task;
            }

            return FakeReply.Result("{}");
        };
        await using var session = new DevToolsSession(browser.Pipe, Fast(o => o.CommandTimeout = TimeSpan.FromMilliseconds(150)));

        await Assert.ThrowsAsync<TimeoutException>(() => session.SendAsync(CdpMethod.InputInsertText, "S", Params(0), TestContext.Current.CancellationToken));
        release.SetResult();
        await session.SendAsync(CdpMethod.InputInsertText, "S", Params(1), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => session.DroppedMessages >= 1);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task Cancelling_a_call_throws_cancellation_not_timeout_and_removes_it_from_the_pending_table()
    {
        await using var browser = new FakeBrowser();
        browser.Handler = _ => Task.FromResult(FakeReply.None);
        await using var session = new DevToolsSession(browser.Pipe, Fast());
        using var cts = new CancellationTokenSource();

        var call = session.SendAsync(CdpMethod.InputInsertText, "S", Params(0), cts.Token);
        await WaitUntilAsync(() => session.PendingCount == 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, session.PendingCount);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task When_the_pipe_ends_every_pending_call_fails_and_later_calls_fail_at_once()
    {
        await using var browser = new FakeBrowser();
        browser.Handler = _ => Task.FromResult(FakeReply.None);
        await using var session = new DevToolsSession(browser.Pipe, Fast(o => o.CommandTimeout = TimeSpan.FromMinutes(1)));

        var calls = Enumerable.Range(0, 5).Select(n => session.SendAsync(CdpMethod.InputInsertText, "S", Params(n), TestContext.Current.CancellationToken)).ToArray();
        await WaitUntilAsync(() => session.PendingCount == 5);
        browser.Close();

        foreach (var call in calls)
        {
            var e = await Assert.ThrowsAsync<PageDriverException>(() => call);
            Assert.Equal(PageDriverFailure.PipeClosed, e.Failure);
        }

        Assert.True(session.IsClosed);
        Assert.Equal(0, session.PendingCount);
        var later = await Assert.ThrowsAsync<PageDriverException>(() => session.SendAsync(CdpMethod.InputInsertText, "S", Params(9), TestContext.Current.CancellationToken));
        Assert.Equal(PageDriverFailure.PipeClosed, later.Failure);
    }

    [Fact]
    public async Task A_framing_failure_fails_the_pending_calls_and_closes_the_session()
    {
        await using var browser = new FakeBrowser();
        browser.Handler = _ => Task.FromResult(FakeReply.None);
        // The reader limit is the screenshot limit's base64 size plus 64 KiB; a message past it is a framing failure.
        await using var session = new DevToolsSession(browser.Pipe, Fast(o => o.MaxScreenshotBytes = 1000));

        var call = session.SendAsync(CdpMethod.InputInsertText, "S", Params(0), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => session.PendingCount == 1);
        // Not awaited: once the reader gives up the pipe stops draining, so a write this big would block on back-pressure.
        _ = browser.SendRawAsync(new byte[200 * 1024].Select(_ => (byte)'a').ToArray());

        var e = await Assert.ThrowsAsync<PageDriverException>(() => call);
        Assert.Equal(PageDriverFailure.PipeClosed, e.Failure);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task A_message_that_ends_in_the_middle_fails_the_session()
    {
        await using var browser = new FakeBrowser();
        browser.Handler = _ => Task.FromResult(FakeReply.None);
        await using var session = new DevToolsSession(browser.Pipe, Fast());

        var call = session.SendAsync(CdpMethod.InputInsertText, "S", Params(0), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => session.PendingCount == 1);
        await browser.SendRawAsync("{\"id\":1,"u8.ToArray());
        browser.Close();

        await Assert.ThrowsAsync<PageDriverException>(() => call);
    }

    [Fact]
    public async Task The_number_of_waiting_calls_is_bounded()
    {
        await using var browser = new FakeBrowser();
        browser.Handler = _ => Task.FromResult(FakeReply.None);
        await using var session = new DevToolsSession(browser.Pipe, Fast(o => o.MaxPendingRequests = 2));

        var a = session.SendAsync(CdpMethod.InputInsertText, "S", Params(0), TestContext.Current.CancellationToken);
        var b = session.SendAsync(CdpMethod.InputInsertText, "S", Params(1), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => session.PendingCount == 2);

        var e = await Assert.ThrowsAsync<PageDriverException>(() => session.SendAsync(CdpMethod.InputInsertText, "S", Params(2), TestContext.Current.CancellationToken));
        Assert.Equal(PageDriverFailure.Busy, e.Failure);
        Assert.Equal(2, session.PendingCount);

        await session.DisposeAsync();
        await Assert.ThrowsAsync<PageDriverException>(() => a);
        await Assert.ThrowsAsync<PageDriverException>(() => b);
    }

    [Fact]
    public async Task An_error_reply_becomes_a_protocol_error_with_the_code_and_without_the_browsers_text()
    {
        await using var browser = new FakeBrowser();
        browser.Handler = _ => Task.FromResult(FakeReply.Error(-32602));
        await using var session = new DevToolsSession(browser.Pipe, Fast());

        var e = await Assert.ThrowsAsync<PageDriverException>(() => session.SendAsync(CdpMethod.InputInsertText, "S", Params(0), TestContext.Current.CancellationToken));

        Assert.Equal(PageDriverFailure.ProtocolError, e.Failure);
        Assert.Equal(-32602, e.ProtocolCode);
        Assert.DoesNotContain("SECRET-PAGE-TEXT", e.ToString(), StringComparison.Ordinal);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task Events_and_junk_are_dropped_and_the_flood_does_not_grow_memory()
    {
        await using var browser = new FakeBrowser();
        await using var session = new DevToolsSession(browser.Pipe, Fast());
        var padding = new string('x', 2048);
        var ev = "{\"method\":\"Page.frameNavigated\",\"params\":{\"pad\":\"" + padding + "\"}}";

        await session.SendAsync(CdpMethod.TargetGetTargets, null, _emptyParams, TestContext.Current.CancellationToken);
        GC.Collect();
        var before = GC.GetTotalMemory(true);

        const int events = 20_000;
        for (var i = 0; i < events; i++)
        {
            await browser.SendEventAsync(ev);
            if (i % 5000 == 0)
            {
                await browser.SendEventAsync("not json at all");
                await browser.SendEventAsync("{\"id\":987654,\"result\":{}}");
            }
        }

        // The pipe is ordered, so once this reply is back the whole flood has been read.
        await session.SendAsync(CdpMethod.TargetGetTargets, null, _emptyParams, TestContext.Current.CancellationToken);
        GC.Collect();
        var after = GC.GetTotalMemory(true);

        Assert.True(session.DroppedMessages >= events);
        Assert.Equal(0, session.PendingCount);
        Assert.False(session.IsClosed);
        // 20,000 events of 2 KiB are about 40 MB; nothing of that may be retained.
        Assert.True(after - before < 8 * 1024 * 1024, $"memory grew by {after - before} bytes");
    }

    [Fact]
    public async Task Disposing_fails_waiting_calls_and_later_calls()
    {
        await using var browser = new FakeBrowser();
        browser.Handler = _ => Task.FromResult(FakeReply.None);
        var session = new DevToolsSession(browser.Pipe, Fast());
        var call = session.SendAsync(CdpMethod.InputInsertText, "S", Params(0), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => session.PendingCount == 1);

        await session.DisposeAsync();

        await Assert.ThrowsAsync<PageDriverException>(() => call);
        await Assert.ThrowsAsync<PageDriverException>(() => session.SendAsync(CdpMethod.InputInsertText, "S", Params(1), TestContext.Current.CancellationToken));
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
