using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Client.Athanor.Bridge;
using Bazaar.Client.Athanor.Drafts;

namespace Bazaar.Client.Athanor.Gtk.Tests;

public sealed class GtkEditorHostTests
{
    private static readonly EditorDocumentId _docId = new("ws", "src/a.cs");
    private static readonly EditorDocument _doc = new(_docId, "class A { }", "rev1", "csharp", false);

    private static GtkEditorHostOptions FastOptions() => new()
    {
        HandshakeTimeout = TimeSpan.FromSeconds(10),
        PingInterval = TimeSpan.Zero,
        PingTimeout = TimeSpan.FromSeconds(10),
        BackoffInitial = TimeSpan.FromMilliseconds(10),
        BackoffMax = TimeSpan.FromMilliseconds(40),
        ExitGrace = TimeSpan.FromMilliseconds(500),
        ShutdownGrace = TimeSpan.FromMilliseconds(500),
        StableAfter = TimeSpan.FromSeconds(60),
        MaxRestarts = 3,
    };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<EditorCrashReason> _crashes = new();
        private readonly SemaphoreSlim _crashSignal = new(0);
        private readonly SemaphoreSlim _readySignal = new(0);

        public Harness(GtkEditorHostOptions? options = null, IEditorDraftStore? drafts = null, Action<FakeLauncher>? configure = null)
        {
            Launcher = new FakeLauncher();
            configure?.Invoke(Launcher);
            Host = new GtkEditorHost(Launcher, options ?? FastOptions(), drafts);
            Host.Crashed += (_, reason) =>
            {
                _crashes.Enqueue(reason);
                _crashSignal.Release();
            };
            Host.Ready += (_, _) => _readySignal.Release();
            Host.Changed += (_, change) => Changes.Enqueue(change);
            Host.SaveRequested += (_, id) => Saves.Enqueue(id);
            Host.KeyPressed += (_, key) => Keys.Enqueue(key);
            Host.ErrorReported += (_, error) => Errors.Enqueue(error);
        }

        public FakeLauncher Launcher { get; }

        public GtkEditorHost Host { get; }

        public ConcurrentQueue<EditorChange> Changes { get; } = new();

        public ConcurrentQueue<EditorDocumentId> Saves { get; } = new();

        public ConcurrentQueue<EditorKeyEvent> Keys { get; } = new();

        public ConcurrentQueue<ErrorMessage> Errors { get; } = new();

        public async Task<EditorCrashReason> NextCrashAsync()
        {
            Assert.True(await _crashSignal.WaitAsync(5000), "No crash was reported in time.");
            _crashes.TryDequeue(out EditorCrashReason reason);
            return reason;
        }

        public async Task NextReadyAsync() => Assert.True(await _readySignal.WaitAsync(5000), "No Ready event in time.");

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + because);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // ---- handshake ----

    [Fact]
    public async Task Start_completes_after_hello_and_ready_and_raises_Ready()
    {
        await using var h = new Harness();

        await h.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EditorHelperState.Running, h.Host.State);
        await h.NextReadyAsync();
        Assert.Equal(1, h.Launcher.LaunchCount);
    }

    [Fact]
    public async Task Start_waits_for_the_page_ready_not_only_hello()
    {
        await using var h = new Harness(configure: l => l.DefaultScript = helper => helper.SendHello());

        Task start = h.Host.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(start.IsCompleted);

        (await h.Launcher.WaitForLaunchAsync(1)).SendReady();
        await start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Handshake_timeout_kills_the_helper_and_reports_unknown()
    {
        GtkEditorHostOptions options = FastOptions() with { HandshakeTimeout = TimeSpan.FromMilliseconds(150) };
        await using var h = new Harness(options, configure: l => l.DefaultScript = null);

        Task start = h.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EditorCrashReason.Unknown, await h.NextCrashAsync());
        Assert.True(h.Launcher.Launched[0].Killed);
        Assert.Contains("handshake", h.Host.LastFailure, StringComparison.OrdinalIgnoreCase);
        Assert.False(start.IsCompleted);
    }

    [Fact]
    public async Task Hello_with_a_different_protocol_version_is_refused()
    {
        await using var h = new Harness(configure: l => l.DefaultScript = helper => helper.SendHello(protocol: 99));

        _ = h.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EditorCrashReason.BridgeFault, await h.NextCrashAsync());
        Assert.Contains("version", h.Host.LastFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_helper_without_the_sandbox_is_refused_unless_the_operator_allows_it()
    {
        await using (var refused = new Harness(configure: l => l.DefaultScript = helper =>
        {
            helper.SendHello(sandbox: false);
            helper.SendReady();
        }))
        {
            _ = refused.Host.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(EditorCrashReason.BridgeFault, await refused.NextCrashAsync());
            Assert.Contains("sandbox", refused.Host.LastFailure, StringComparison.OrdinalIgnoreCase);
        }

        await using var allowed = new Harness(FastOptions() with { AllowNoSandbox = true }, configure: l => l.DefaultScript = helper =>
        {
            helper.SendHello(sandbox: false);
            helper.SendReady();
        });
        await allowed.Host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EditorHelperState.Running, allowed.Host.State);
    }

    [Theory]
    [InlineData("""{"type":"ready","seq":1}""")]
    [InlineData("not json at all")]
    [InlineData("""{"seq":1}""")]
    public async Task The_first_line_must_be_helper_hello(string firstLine)
    {
        await using var h = new Harness(configure: l => l.DefaultScript = helper => helper.Send(firstLine));

        _ = h.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EditorCrashReason.BridgeFault, await h.NextCrashAsync());
    }

    // ---- message round trip ----

    [Fact]
    public async Task Open_sends_an_open_message_with_seq_one_and_later_messages_rise()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper helper = h.Launcher.Launched[0];

        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);
        await h.Host.FocusAsync(TestContext.Current.CancellationToken);
        await h.Host.SetThemeAsync("{\"base\":\"vs-dark\"}", TestContext.Current.CancellationToken);
        await h.Host.SetReadOnlyAsync(_docId, true, TestContext.Current.CancellationToken);
        await h.Host.ShowDiffAsync("a", "b", "plaintext", TestContext.Current.CancellationToken);
        await h.Host.CloseAsync(_docId, TestContext.Current.CancellationToken);

        JsonElement open = await helper.ReadJsonFromProxyAsync();
        Assert.Equal("open", open.GetProperty("type").GetString());
        Assert.Equal(1, open.GetProperty("seq").GetInt64());
        Assert.Equal("ws:src/a.cs", open.GetProperty("documentId").GetString());
        Assert.Equal("class A { }", open.GetProperty("content").GetString());
        Assert.Equal("rev1", open.GetProperty("revision").GetString());
        Assert.Equal("csharp", open.GetProperty("languageId").GetString());

        string[] expected = ["focus", "setTheme", "setReadOnly", "showDiff", "close"];
        long last = 1;
        foreach (string type in expected)
        {
            JsonElement m = await helper.ReadJsonFromProxyAsync();
            Assert.Equal(type, m.GetProperty("type").GetString());
            long seq = m.GetProperty("seq").GetInt64();
            Assert.True(seq > last, $"seq {seq} did not rise above {last}");
            last = seq;
        }
    }

    [Fact]
    public async Task Editor_messages_become_host_events_and_track_the_latest_content()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper helper = h.Launcher.Launched[0];
        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);

        helper.SendChanged("ws:src/a.cs", 1, "class A { int x; }");
        helper.SendEditor("saveRequested", "\"documentId\":\"ws:src/a.cs\"");
        helper.SendEditor("keyPressed", "\"key\":\"Alt+KeyM\",\"shift\":false,\"alt\":true,\"meta\":false,\"ctrl\":false");
        helper.SendEditor("selection", "\"documentId\":\"ws:src/a.cs\",\"startLine\":1,\"startColumn\":1,\"endLine\":1,\"endColumn\":2");
        helper.SendEditor("error", "\"documentId\":null,\"code\":\"monaco\",\"message\":\"boom\"");

        await WaitUntilAsync(() => !h.Errors.IsEmpty && !h.Keys.IsEmpty && !h.Saves.IsEmpty && !h.Changes.IsEmpty, "all four events");
        Assert.True(h.Changes.TryPeek(out EditorChange? change));
        Assert.Equal(_docId, change.Id);
        Assert.Equal(1, change.Version);
        Assert.Equal("class A { int x; }", change.Content);
        Assert.True(change.Dirty);
        Assert.True(h.Saves.TryPeek(out EditorDocumentId save));
        Assert.Equal(_docId, save);
        Assert.True(h.Keys.TryPeek(out EditorKeyEvent? key));
        Assert.Equal("Alt+KeyM", key.Key);
        Assert.True(key.Alt);
        Assert.True(h.Errors.TryPeek(out ErrorMessage? error));
        Assert.Equal("monaco", error.Code);
        Assert.Equal(EditorHelperState.Running, h.Host.State);
    }

    [Fact]
    public async Task A_changed_message_for_an_unknown_document_is_ignored()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);

        h.Launcher.Launched[0].SendChanged("ws:other.cs", 1, "x");
        h.Launcher.Launched[0].SendEditor("saveRequested", "\"documentId\":\"ws:other.cs\"");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(h.Changes.IsEmpty);
        Assert.True(h.Saves.IsEmpty);
        Assert.Equal(EditorHelperState.Running, h.Host.State);
    }

    [Fact]
    public async Task Window_placement_is_sent_as_a_control_message()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper helper = h.Launcher.Launched[0];

        await h.Host.SetWindowAsync(new EditorWindowPlacement(10, 20, 900, 600, true, "Athanor"), TestContext.Current.CancellationToken);

        JsonElement m = await helper.ReadJsonFromProxyAsync();
        Assert.Equal("helper.window", m.GetProperty("type").GetString());
        Assert.Equal(10, m.GetProperty("x").GetInt32());
        Assert.Equal(20, m.GetProperty("y").GetInt32());
        Assert.Equal(900, m.GetProperty("width").GetInt32());
        Assert.Equal(600, m.GetProperty("height").GetInt32());
        Assert.True(m.GetProperty("visible").GetBoolean());
        Assert.Equal("Athanor", m.GetProperty("title").GetString());
    }

    [Fact]
    public async Task WindowClosed_from_the_helper_raises_the_event_and_keeps_running()
    {
        await using var h = new Harness();
        var closed = new TaskCompletionSource();
        h.Host.WindowClosed += (_, _) => closed.TrySetResult();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);

        h.Launcher.Launched[0].Send("""{"type":"helper.windowClosed"}""");

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(EditorHelperState.Running, h.Host.State);
    }

    [Fact]
    public async Task ShowDiff_without_a_running_helper_throws()
    {
        await using var h = new Harness();

        await Assert.ThrowsAsync<EditorHelperException>(() => h.Host.ShowDiffAsync("a", "b", "plaintext", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_oversize_open_is_refused_before_it_changes_state()
    {
        await using var h = new Harness(FastOptions() with { MaxPayloadBytes = 1024 });
        await h.Host.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Host.OpenAsync(_doc with { Content = new string('x', 5000) }, TestContext.Current.CancellationToken));
        Assert.Equal(EditorHelperState.Running, h.Host.State);
    }

    // ---- sequence guard and protocol errors ----

    [Fact]
    public async Task A_repeated_or_lower_seq_from_the_editor_is_a_bridge_fault()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper helper = h.Launcher.Launched[0];
        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);

        helper.SendEditorWithSeq("saveRequested", 5, "\"documentId\":\"ws:src/a.cs\"");
        helper.SendEditorWithSeq("saveRequested", 5, "\"documentId\":\"ws:src/a.cs\"");

        Assert.Equal(EditorCrashReason.BridgeFault, await h.NextCrashAsync());
        Assert.True(helper.Killed);
        Assert.Contains("sequence", h.Host.LastFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Saves);
    }

    [Theory]
    [InlineData("""{"type":"bogus","seq":50}""")]
    [InlineData("""{"type":"changed","seq":50,"documentId":"ws:src/a.cs"}""")]
    [InlineData("""{"type":"selection","seq":50,"documentId":"ws:src/a.cs","startLine":0,"startColumn":1,"endLine":1,"endColumn":1}""")]
    [InlineData("{broken")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"helper.unknown"}""")]
    [InlineData("""{"type":"helper.hello","protocol":1,"sandbox":true}""")]
    public async Task Protocol_violations_kill_the_helper_and_report_a_bridge_fault(string badLine)
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper helper = h.Launcher.Launched[0];

        helper.Send(badLine);

        Assert.Equal(EditorCrashReason.BridgeFault, await h.NextCrashAsync());
        Assert.True(helper.Killed);
    }

    [Fact]
    public async Task A_line_over_the_payload_limit_is_a_bridge_fault()
    {
        await using var h = new Harness(FastOptions() with { MaxPayloadBytes = 2048 });
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper helper = h.Launcher.Launched[0];
        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);

        helper.SendChanged("ws:src/a.cs", 1, new string('x', 5000));

        Assert.Equal(EditorCrashReason.BridgeFault, await h.NextCrashAsync());
    }

    [Fact]
    public async Task A_second_ready_is_a_bridge_fault()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);

        h.Launcher.Launched[0].SendReady();

        Assert.Equal(EditorCrashReason.BridgeFault, await h.NextCrashAsync());
    }

    [Fact]
    public async Task An_editor_message_before_ready_is_a_bridge_fault()
    {
        await using var h = new Harness(configure: l => l.DefaultScript = helper =>
        {
            helper.SendHello();
            helper.SendEditor("keyPressed", "\"key\":\"A\",\"shift\":false,\"alt\":false,\"meta\":false,\"ctrl\":false");
        });

        _ = h.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EditorCrashReason.BridgeFault, await h.NextCrashAsync());
    }

    // ---- crash, restart, drafts ----

    [Fact]
    public async Task A_helper_that_exits_is_a_renderer_exit_and_is_restarted_with_state_replayed()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper first = h.Launcher.Launched[0];
        await h.Host.SetThemeAsync("{\"base\":\"vs-dark\"}", TestContext.Current.CancellationToken);
        await h.Host.SetWindowAsync(new EditorWindowPlacement(0, 0, 800, 500, true), TestContext.Current.CancellationToken);
        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);
        first.SendChanged("ws:src/a.cs", 1, "class A { int typed; }");
        await WaitUntilAsync(() => !h.Changes.IsEmpty, "the change");

        first.Exit(139);

        Assert.Equal(EditorCrashReason.RendererExited, await h.NextCrashAsync());
        FakeHelper second = await h.Launcher.WaitForLaunchAsync(2);
        await h.NextReadyAsync();
        await h.NextReadyAsync();

        JsonElement window = await second.ReadJsonFromProxyAsync();
        Assert.Equal("helper.window", window.GetProperty("type").GetString());
        JsonElement theme = await second.ReadJsonFromProxyAsync();
        Assert.Equal("setTheme", theme.GetProperty("type").GetString());
        Assert.Equal(1, theme.GetProperty("seq").GetInt64());
        JsonElement open = await second.ReadJsonFromProxyAsync();
        Assert.Equal("open", open.GetProperty("type").GetString());
        Assert.Equal("class A { int typed; }", open.GetProperty("content").GetString());
        Assert.Equal("rev1", open.GetProperty("revision").GetString());
        Assert.Equal(1, h.Host.TotalRestarts);
        Assert.Equal(EditorHelperState.Running, h.Host.State);
        Assert.Contains("139", h.Host.LastFailure ?? "139", StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_a_restart_the_editor_sequence_guard_starts_fresh()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);
        h.Launcher.Launched[0].SendEditorWithSeq("saveRequested", 40, "\"documentId\":\"ws:src/a.cs\"");
        await WaitUntilAsync(() => !h.Saves.IsEmpty, "save event");
        h.Launcher.Launched[0].Exit(1);
        await h.NextCrashAsync();
        FakeHelper second = await h.Launcher.WaitForLaunchAsync(2);
        await WaitUntilAsync(() => h.Host.State == EditorHelperState.Running && h.Host.TotalRestarts == 1, "restart");

        second.SendEditorWithSeq("saveRequested", 2, "\"documentId\":\"ws:src/a.cs\"");

        await WaitUntilAsync(() => h.Saves.Count == 2, "second save event");
    }

    [Fact]
    public async Task Dirty_documents_are_written_to_the_draft_store_when_the_helper_dies()
    {
        var drafts = new InMemoryEditorDraftStore();
        await using var h = new Harness(drafts: drafts);
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);
        h.Launcher.Launched[0].SendChanged("ws:src/a.cs", 3, "unsaved text");
        await WaitUntilAsync(() => !h.Changes.IsEmpty, "the change");

        h.Launcher.Launched[0].Exit(1);
        await h.NextCrashAsync();

        EditorDraft? draft = await drafts.ReadAsync(_docId, TestContext.Current.CancellationToken);
        Assert.NotNull(draft);
        Assert.Equal("unsaved text", draft.Content);
        Assert.Equal("rev1", draft.BaseRevision);
    }

    [Fact]
    public async Task Clean_documents_write_no_draft()
    {
        var drafts = new InMemoryEditorDraftStore();
        await using var h = new Harness(drafts: drafts);
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);

        h.Launcher.Launched[0].Exit(1);
        await h.NextCrashAsync();

        Assert.Null(await drafts.ReadAsync(_docId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_fatal_control_message_is_a_renderer_exit()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);

        h.Launcher.Launched[0].Send("""{"type":"helper.fatal","code":"web_process_terminated","message":"gone"}""");

        Assert.Equal(EditorCrashReason.RendererExited, await h.NextCrashAsync());
        Assert.Contains("web_process_terminated", h.Host.LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operations_while_the_helper_is_down_are_recorded_and_replayed()
    {
        GtkEditorHostOptions options = FastOptions() with { BackoffInitial = TimeSpan.FromMilliseconds(300), BackoffMax = TimeSpan.FromMilliseconds(300) };
        await using var h = new Harness(options);
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        h.Launcher.Launched[0].Exit(1);
        await h.NextCrashAsync();
        Assert.Equal(EditorHelperState.Backoff, h.Host.State);

        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);
        await h.Host.SetThemeAsync("{\"t\":1}", TestContext.Current.CancellationToken);

        FakeHelper second = await h.Launcher.WaitForLaunchAsync(2);
        JsonElement theme = await second.ReadJsonFromProxyAsync();
        JsonElement open = await second.ReadJsonFromProxyAsync();
        Assert.Equal("setTheme", theme.GetProperty("type").GetString());
        Assert.Equal("open", open.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_closed_document_is_not_replayed()
    {
        await using var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        await h.Host.OpenAsync(_doc, TestContext.Current.CancellationToken);
        await h.Host.CloseAsync(_docId, TestContext.Current.CancellationToken);

        h.Launcher.Launched[0].Exit(1);
        await h.NextCrashAsync();
        FakeHelper second = await h.Launcher.WaitForLaunchAsync(2);
        await WaitUntilAsync(() => h.Host.State == EditorHelperState.Running && h.Host.TotalRestarts == 1, "restart");

        Assert.False(second.HasPendingFromProxy);
    }

    [Fact]
    public async Task Repeated_failures_give_up_then_RestartAsync_starts_again()
    {
        GtkEditorHostOptions options = FastOptions() with { MaxRestarts = 2 };
        await using var h = new Harness(options, configure: l => l.DefaultScript = helper => helper.Exit(7));

        Task start = h.Host.StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<EditorHelperException>(() => start);

        Assert.Equal(EditorHelperState.Faulted, h.Host.State);
        Assert.Equal(3, h.Launcher.LaunchCount);

        h.Launcher.DefaultScript = helper =>
        {
            helper.AutoPong = true;
            helper.SendHandshake();
        };
        await h.Host.RestartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EditorHelperState.Running, h.Host.State);
    }

    [Fact]
    public async Task Backoff_doubles_up_to_the_maximum()
    {
        GtkEditorHostOptions options = FastOptions() with
        {
            MaxRestarts = 4,
            BackoffInitial = TimeSpan.FromMilliseconds(60),
            BackoffMax = TimeSpan.FromMilliseconds(120),
        };
        var stamps = new ConcurrentQueue<DateTime>();
        await using var h = new Harness(options, configure: l => l.DefaultScript = helper =>
        {
            stamps.Enqueue(DateTime.UtcNow);
            helper.Exit(1);
        });

        await Assert.ThrowsAsync<EditorHelperException>(() => h.Host.StartAsync(TestContext.Current.CancellationToken));

        DateTime[] times = [.. stamps];
        Assert.Equal(5, times.Length);
        double[] gaps = [.. times.Zip(times.Skip(1), (a, b) => (b - a).TotalMilliseconds)];
        Assert.True(gaps[0] >= 55, $"first gap {gaps[0]}");
        Assert.True(gaps[1] >= 110, $"second gap {gaps[1]}");
        Assert.True(gaps[3] >= 110, $"capped gap {gaps[3]}");
    }

    [Fact]
    public async Task A_launch_failure_is_reported_as_unknown_and_retried()
    {
        await using var h = new Harness(configure: l =>
        {
            l.LaunchFailure = new IOException("no such file");
        });

        _ = h.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EditorCrashReason.Unknown, await h.NextCrashAsync());
        Assert.Contains("no such file", h.Host.LastFailure, StringComparison.Ordinal);
        h.Launcher.LaunchFailure = null;
        await h.NextReadyAsync();
        Assert.Equal(EditorHelperState.Running, h.Host.State);
    }

    // ---- timeouts ----

    [Fact]
    public async Task A_helper_that_stops_answering_pings_is_killed_as_hung()
    {
        GtkEditorHostOptions options = FastOptions() with { PingInterval = TimeSpan.FromMilliseconds(40), PingTimeout = TimeSpan.FromMilliseconds(150) };
        await using var h = new Harness(options, configure: l => l.DefaultScript = helper => helper.SendHandshake());

        await h.Host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EditorCrashReason.Unknown, await h.NextCrashAsync());
        Assert.True(h.Launcher.Launched[0].Killed);
        Assert.Contains("ping", h.Host.LastFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_helper_that_answers_pings_stays_up()
    {
        GtkEditorHostOptions options = FastOptions() with { PingInterval = TimeSpan.FromMilliseconds(30), PingTimeout = TimeSpan.FromMilliseconds(300) };
        await using var h = new Harness(options);

        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(700, TestContext.Current.CancellationToken);

        Assert.Equal(EditorHelperState.Running, h.Host.State);
        Assert.Equal(1, h.Launcher.LaunchCount);
    }

    // ---- shutdown ----

    [Fact]
    public async Task Dispose_asks_the_helper_to_shut_down_and_does_not_report_a_crash()
    {
        var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper helper = h.Launcher.Launched[0];
        var crashed = false;
        h.Host.Crashed += (_, _) => crashed = true;

        Task dispose = h.Host.DisposeAsync().AsTask();
        JsonElement m = await helper.ReadJsonFromProxyAsync();
        Assert.Equal("helper.shutdown", m.GetProperty("type").GetString());
        helper.Exit(0);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(crashed);
        Assert.Equal(EditorHelperState.Disposed, h.Host.State);
        Assert.Equal(1, h.Launcher.LaunchCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_kills_a_helper_that_ignores_shutdown()
    {
        var h = new Harness();
        await h.Host.StartAsync(TestContext.Current.CancellationToken);
        FakeHelper helper = h.Launcher.Launched[0];

        await h.Host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(helper.Killed || helper.Disposed);
        Assert.Equal(EditorHelperState.Disposed, h.Host.State);
    }

    [Fact]
    public async Task Dispose_before_start_is_safe_and_twice_is_safe()
    {
        var host = new GtkEditorHost(new FakeLauncher(), FastOptions());

        await host.DisposeAsync();
        await host.DisposeAsync();

        Assert.Equal(EditorHelperState.Disposed, host.State);
    }

    [Fact]
    public void Options_reject_nonsense()
    {
        Assert.Throws<ArgumentException>(() => new GtkEditorHostOptions { HandshakeTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentException>(() => new GtkEditorHostOptions { MaxRestarts = -1 }.Validate());
        Assert.Throws<ArgumentException>(() => new GtkEditorHostOptions { MaxPayloadBytes = 0 }.Validate());
        new GtkEditorHostOptions().Validate();
        Assert.Throws<ArgumentException>(() => new GtkEditorHost(new FakeLauncher(), new GtkEditorHostOptions { PingTimeout = TimeSpan.Zero }));
    }
}
