using System.Text.Json;
using Bazaar.Client.Athanor.Bridge;
using Bazaar.Client.Athanor.Hosting;

namespace Bazaar.Client.Athanor.Tests.Hosting;

public sealed class BridgeEditorHostTests
{
    private static readonly JsonSerializerOptions _wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Ready(long seq = 1) => $"{{\"type\":\"ready\",\"seq\":{seq}}}";

    private static string Changed(long seq, string id, long version, string content, bool dirty = true) =>
        JsonSerializer.Serialize(new { type = "changed", seq, documentId = id, version, content, dirty }, _wire);

    private static string Key(long seq, string key, bool alt = false, bool ctrl = false) =>
        JsonSerializer.Serialize(new { type = "keyPressed", seq, key, shift = false, alt, meta = false, ctrl }, _wire);

    private static async Task<(BridgeEditorHost Host, FakeTransport Transport)> ReadyHostAsync(
        IEditorKeyHandoff? handoff = null, IClaimedKeySource? claims = null)
    {
        var transport = new FakeTransport();
        var host = new BridgeEditorHost(transport, handoff, claims);
        transport.Receive(Ready());
        await transport.WaitForPostsAsync(claims is null ? 0 : 1);
        return (host, transport);
    }

    [Fact]
    public async Task Messages_sent_before_ready_wait_and_flush_in_order_with_rising_seq()
    {
        var transport = new FakeTransport();
        await using var host = new BridgeEditorHost(transport);
        EditorDocument doc = HostingFixtures.Document("a.txt");

        await host.OpenAsync(doc, CancellationToken.None);
        await host.SetThemeAsync("{\"base\":\"vs\"}", CancellationToken.None);
        await host.FocusAsync(CancellationToken.None);
        Assert.Empty(transport.Posts);

        transport.Receive(Ready());
        await transport.WaitForPostsAsync(3);

        Assert.Equal(["open", "setTheme", "focus"], transport.Types());
        Assert.Equal([1L, 2L, 3L], transport.Seqs());
    }

    [Fact]
    public async Task Open_sends_the_document_with_its_id_revision_and_language()
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;

        await host.OpenAsync(HostingFixtures.Document("src/a.cs", "x") with { LanguageId = "csharp", ReadOnly = true }, CancellationToken.None);

        using JsonDocument json = JsonDocument.Parse(transport.Posts[0]);
        JsonElement root = json.RootElement;
        Assert.Equal("ws:src/a.cs", root.GetProperty("documentId").GetString());
        Assert.Equal("rev-1", root.GetProperty("revision").GetString());
        Assert.Equal("csharp", root.GetProperty("languageId").GetString());
        Assert.True(root.GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task Ready_event_fires_once_the_first_ready_is_handled()
    {
        var transport = new FakeTransport();
        await using var host = new BridgeEditorHost(transport);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Ready += (_, _) => raised.TrySetResult();

        transport.Receive(Ready());

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Changed_maps_to_the_document_id_and_keeps_the_version()
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;
        await host.OpenAsync(HostingFixtures.Document("a.txt"), CancellationToken.None);
        EditorChange? seen = null;
        host.Changed += (_, c) => seen = c;

        transport.Receive(Changed(2, "ws:a.txt", 7, "typed"));

        Assert.Equal(new EditorChange(new EditorDocumentId("ws", "a.txt"), 7, "typed", true), seen);
    }

    [Fact]
    public async Task Changed_for_an_unopened_document_is_refused()
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;
        bool raised = false;
        host.Changed += (_, _) => raised = true;

        transport.Receive(Changed(2, "ws:never.txt", 1, "x"));

        Assert.False(raised);
        Assert.Equal(1, host.RejectedInboundCount);
    }

    [Fact]
    public async Task Stale_editor_seq_is_rejected_and_does_not_reach_the_host()
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;
        await host.OpenAsync(HostingFixtures.Document("a.txt"), CancellationToken.None);
        int changes = 0;
        host.Changed += (_, _) => changes++;

        transport.Receive(Changed(5, "ws:a.txt", 1, "a"));
        transport.Receive(Changed(5, "ws:a.txt", 2, "b"));
        transport.Receive(Changed(3, "ws:a.txt", 3, "c"));

        Assert.Equal(1, changes);
        Assert.Equal(2, host.RejectedInboundCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"bogus\",\"seq\":9}")]
    [InlineData("{\"type\":\"changed\",\"seq\":9}")]
    public async Task Malformed_or_unknown_editor_messages_are_counted_and_ignored(string json)
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;

        transport.Receive(json);

        Assert.Equal(1, host.RejectedInboundCount);
        Assert.False(host.IsCrashed);
    }

    [Fact]
    public async Task Save_requested_maps_to_the_document_id()
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;
        await host.OpenAsync(HostingFixtures.Document("a.txt"), CancellationToken.None);
        EditorDocumentId? saved = null;
        host.SaveRequested += (_, id) => saved = id;

        transport.Receive("{\"type\":\"saveRequested\",\"seq\":2,\"documentId\":\"ws:a.txt\"}");

        Assert.Equal(new EditorDocumentId("ws", "a.txt"), saved);
    }

    [Fact]
    public async Task Claimed_key_is_offered_to_the_router_and_swallowed_when_it_claims()
    {
        var offered = new List<EditorKeyEvent>();
        var handoff = new RouterKeyHandoff(k =>
        {
            offered.Add(k);
            return true;
        });
        var (host, transport) = await ReadyHostAsync(handoff);
        await using var _ = host;
        bool raised = false;
        host.KeyPressed += (_, _) => raised = true;

        transport.Receive(Key(2, "KeyM", alt: true));

        Assert.Single(offered);
        Assert.Equal("KeyM", offered[0].Key);
        Assert.True(offered[0].Alt);
        Assert.False(raised);
    }

    [Fact]
    public async Task Key_the_router_no_longer_claims_is_raised_as_key_pressed()
    {
        var handoff = new RouterKeyHandoff(_ => false);
        var (host, transport) = await ReadyHostAsync(handoff);
        await using var _ = host;
        EditorKeyEvent? seen = null;
        host.KeyPressed += (_, k) => seen = k;

        transport.Receive(Key(2, "F6"));

        Assert.Equal("F6", seen?.Key);
    }

    [Fact]
    public async Task A_ctrl_key_report_is_dropped_and_never_reaches_the_router()
    {
        int asked = 0;
        var handoff = new RouterKeyHandoff(_ =>
        {
            asked++;
            return true;
        });
        var (host, transport) = await ReadyHostAsync(handoff);
        await using var _ = host;
        bool raised = false;
        host.KeyPressed += (_, _) => raised = true;

        transport.Receive(Key(2, "KeyS", ctrl: true));

        Assert.Equal(0, asked);
        Assert.False(raised);
    }

    [Fact]
    public async Task Claim_table_is_sent_on_ready_without_ctrl_chords_and_again_when_it_changes()
    {
        var claims = new FakeClaims("Alt+KeyM", "Ctrl+KeyK", "F6", "Alt+KeyM", " ", "Control+Shift+KeyP");
        var (host, transport) = await ReadyHostAsync(claims: claims);
        await using var _ = host;

        Assert.Equal(["setClaimedKeys"], transport.Types());
        Assert.Equal(["Alt+KeyM", "F6"], transport.ClaimedKeys(0));

        claims.Set("Meta+KeyJ");
        await transport.WaitForPostsAsync(2);

        Assert.Equal(["Meta+KeyJ"], transport.ClaimedKeys(1));
    }

    [Fact]
    public async Task Renderer_crash_raises_crashed_and_blocks_surface_calls_until_the_page_is_ready_again()
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;
        EditorCrashReason? reason = null;
        host.Crashed += (_, r) => reason = r;

        transport.Fault(EditorCrashReason.RendererExited);

        Assert.Equal(EditorCrashReason.RendererExited, reason);
        Assert.True(host.IsCrashed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.FocusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reload_after_a_crash_restarts_seq_and_resends_theme_and_open_documents_with_latest_content()
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;
        await host.SetThemeAsync("{\"base\":\"vs\"}", CancellationToken.None);
        await host.OpenAsync(HostingFixtures.Document("a.txt", "original"), CancellationToken.None);
        transport.Receive(Changed(2, "ws:a.txt", 1, "typed since open"));
        int before = transport.Posts.Count;

        transport.Fault(EditorCrashReason.RendererExited);
        transport.Receive(Ready(1)); // the reloaded page starts at seq 1 again
        await transport.WaitForPostsAsync(before + 2);

        Assert.False(host.IsCrashed);
        IReadOnlyList<string> types = transport.Types().Skip(before).ToArray();
        Assert.Equal(["setTheme", "open"], types);
        Assert.Equal([1L, 2L], transport.Seqs().Skip(before).ToArray());
        using JsonDocument open = JsonDocument.Parse(transport.Posts[before + 1]);
        Assert.Equal("typed since open", open.RootElement.GetProperty("content").GetString());

        await host.FocusAsync(CancellationToken.None);
        Assert.Equal(3L, transport.Seqs()[^1]);

        transport.Receive(Changed(2, "ws:a.txt", 2, "after reload"));
        Assert.Equal(0, host.RejectedInboundCount);
    }

    [Fact]
    public async Task Editor_error_message_is_reported_without_crashing_the_host()
    {
        var (host, transport) = await ReadyHostAsync();
        await using var _ = host;
        ErrorMessage? seen = null;
        host.ErrorReported += (_, e) => seen = e;

        transport.Receive("{\"type\":\"error\",\"seq\":2,\"code\":\"stale\",\"message\":\"stale seq\"}");

        Assert.Equal("stale", seen?.Code);
        Assert.False(host.IsCrashed);
    }

    [Fact]
    public async Task Disposed_host_stops_listening_and_rejects_calls()
    {
        var transport = new FakeTransport();
        var host = new BridgeEditorHost(transport);
        await host.DisposeAsync();

        Assert.True(transport.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.FocusAsync(CancellationToken.None));
        transport.Receive(Ready()); // must not throw
    }

    [Fact]
    public void Safe_chords_drop_blank_duplicate_and_ctrl_entries()
    {
        string[] result = BridgeEditorHost.SafeChords(["Alt+KeyA", "alt+KeyA", "Ctrl+Alt+KeyB", "Alt+Ctrl+KeyC", "", "Shift+F2", "Alt+KeyA"]);

        Assert.Equal(["Alt+KeyA", "alt+KeyA", "Shift+F2"], result);
    }

    [Fact]
    public void Claimed_keys_message_round_trips_through_the_codec()
    {
        var codec = new BridgeCodec();
        string json = codec.Encode(new SetClaimedKeysMessage(4, ["Alt+KeyM", "F6"]));

        HostMessage? decoded = JsonSerializer.Deserialize<HostMessage>(json, _wire);

        var message = Assert.IsType<SetClaimedKeysMessage>(decoded);
        Assert.Equal(4, message.Seq);
        Assert.Equal(["Alt+KeyM", "F6"], message.Keys);
    }

    [Fact]
    public async Task Request_save_raises_save_requested_for_an_open_document_only()
    {
        var (host, _) = await ReadyHostAsync();
        await using var _d = host;
        EditorDocument doc = HostingFixtures.Document("a.txt");
        var saves = new List<EditorDocumentId>();
        host.SaveRequested += (_, id) => saves.Add(id);

        Assert.False(host.RequestSave(doc.Id));
        await host.OpenAsync(doc, CancellationToken.None);
        Assert.True(host.RequestSave(doc.Id));

        Assert.Equal([doc.Id], saves);
        await host.CloseAsync(doc.Id, CancellationToken.None);
        Assert.False(host.RequestSave(doc.Id));
    }

    private sealed class FakeClaims(params string[] chords) : IClaimedKeySource
    {
        private string[] _chords = chords;

        public IReadOnlyList<string> Chords => _chords;

        public event EventHandler? Changed;

        public void Set(params string[] chords)
        {
            _chords = chords;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeTransport : IEditorBridgeTransport
    {
        private readonly object _gate = new();

        public event EventHandler<string>? MessageReceived;

        public event EventHandler<EditorCrashReason>? Faulted;

        public List<string> Posts { get; } = [];

        public bool Disposed { get; private set; }

        public void Receive(string json) => MessageReceived?.Invoke(this, json);

        public void Fault(EditorCrashReason reason) => Faulted?.Invoke(this, reason);

        public ValueTask PostAsync(string json, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Posts.Add(json);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public async Task WaitForPostsAsync(int count)
        {
            for (int i = 0; i < 200; i++)
            {
                lock (_gate)
                {
                    if (Posts.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Expected {count} posts, saw {Posts.Count}.");
        }

        public string[] Types() => Parse(root => root.GetProperty("type").GetString()!);

        public long[] Seqs() => Parse(root => root.GetProperty("seq").GetInt64());

        public string[] ClaimedKeys(int index)
        {
            using JsonDocument json = JsonDocument.Parse(Posts.Where(p => p.Contains("setClaimedKeys")).ElementAt(index));
            return [.. json.RootElement.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!)];
        }

        private T[] Parse<T>(Func<JsonElement, T> read)
        {
            lock (_gate)
            {
                return [.. Posts.Select(p =>
                {
                    using JsonDocument json = JsonDocument.Parse(p);
                    return read(json.RootElement);
                })];
            }
        }
    }
}
