using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Athanor.EndToEnd.Tests;

/// <summary>
/// The Athanor pane, <see cref="WireEditorFileSource"/>, the real host wire handlers and the directory workspace store joined
/// in one process (doc 57 section 6, PROPOSED): open a Vault file, edit it, save it, and handle conflict and offline.
/// </summary>
public sealed class EditorEndToEndTests : IDisposable
{
    private static readonly JsonWireCodec _codec = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly EditorDocumentId _note = new("vault", "notes/a.md");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "athanor-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FakeChecker _checker = new();
    private readonly ServiceProvider _server;
    private readonly InProcessRequester _requester;
    private readonly InMemoryEditorDraftStore _drafts = new();
    private readonly PlainTextFallbackHost _host = new();
    private readonly AthanorPaneViewModel _pane;

    public EditorEndToEndTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        File.WriteAllText(Path.Combine(_root, "notes", "a.md"), "hello");
        _checker.Allow("read", "vault.write");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Athanor:Workspaces:0:Id"] = "vault",
            ["Bazaar:Athanor:Workspaces:0:Kind"] = "Vault",
            ["Bazaar:Athanor:Workspaces:0:Path"] = _root,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ISettingStore>(new EmptySettings());
        services.AddSingleton<IPermissionChecker>(_checker);
        services.AddSingleton<IAuditLog>(new NullAudit());
        services.AddBazaarHostAthanor(configuration);
        _server = services.BuildServiceProvider(validateScopes: true);

        _requester = new InProcessRequester(_server.GetServices<IWireRequestHandler>().ToList(), new HumanConnection(Guid.CreateVersion7()));
        _pane = new AthanorPaneViewModel("Athanor", _host, new WireEditorFileSource(_requester), _drafts);
    }

    public void Dispose()
    {
        _pane.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _server.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string DiskText() => File.ReadAllText(Path.Combine(_root, "notes", "a.md"));

    [Fact]
    public async Task Open_edit_and_save_writes_the_vault_file_and_clears_the_dirty_marker()
    {
        Assert.True(await _pane.OpenAsync(_note, Ct));
        Assert.Equal("hello", _pane.ActiveContent);
        Assert.False(_pane.ActiveTab!.ReadOnly);

        _pane.EditActive("hello world");
        Assert.True(_pane.ActiveTab!.Dirty);
        var outcome = await _pane.SaveActiveAsync(Ct);

        Assert.IsType<SaveOutcome.Saved>(outcome);
        Assert.Equal("hello world", DiskText());
        Assert.False(_pane.ActiveTab!.Dirty);
        Assert.Null(await _drafts.ReadAsync(_note, Ct));
    }

    [Fact]
    public async Task A_missing_file_does_not_open()
    {
        Assert.False(await _pane.OpenAsync(new EditorDocumentId("vault", "notes/missing.md"), Ct));
        Assert.Empty(_pane.Tabs);
    }

    [Fact]
    public async Task Without_the_write_scope_the_file_opens_read_only_and_typing_is_ignored()
    {
        _checker.Allow("read");

        Assert.True(await _pane.OpenAsync(_note, Ct));
        _pane.EditActive("nope");

        Assert.True(_pane.ActiveTab!.ReadOnly);
        Assert.Equal("hello", DiskText());
    }

    [Fact]
    public async Task A_save_over_a_newer_server_copy_conflicts_and_keep_mine_then_saves_on_top()
    {
        await _pane.OpenAsync(_note, Ct);
        _pane.EditActive("mine");
        await File.WriteAllTextAsync(Path.Combine(_root, "notes", "a.md"), "theirs", Ct);

        var first = await _pane.SaveActiveAsync(Ct);

        var conflict = Assert.IsType<SaveOutcome.Conflict>(first);
        Assert.Equal("theirs", conflict.ServerContent);
        Assert.Equal("theirs", DiskText());
        Assert.NotNull(_pane.ActiveConflict);
        Assert.NotNull(await _drafts.ReadAsync(_note, Ct));

        await _pane.KeepMineAsync();
        var second = await _pane.SaveActiveAsync(Ct);

        Assert.IsType<SaveOutcome.Saved>(second);
        Assert.Equal("mine", DiskText());
        Assert.Null(_pane.ActiveConflict);
        Assert.Null(await _drafts.ReadAsync(_note, Ct));
    }

    [Fact]
    public async Task While_the_server_is_unreachable_the_edit_stays_as_a_draft_and_a_later_save_succeeds()
    {
        await _pane.OpenAsync(_note, Ct);
        _pane.EditActive("typed offline");
        _requester.Offline = true;

        var offline = await _pane.SaveActiveAsync(Ct);

        Assert.IsType<SaveOutcome.Unavailable>(offline);
        Assert.NotNull(_pane.ActiveUnavailableReason);
        Assert.Equal("hello", DiskText());
        var draft = await _drafts.ReadAsync(_note, Ct);
        Assert.Equal("typed offline", draft!.Content);

        _requester.Offline = false;
        var online = await _pane.SaveActiveAsync(Ct);

        Assert.IsType<SaveOutcome.Saved>(online);
        Assert.Equal("typed offline", DiskText());
        Assert.Null(await _drafts.ReadAsync(_note, Ct));
    }

    [Fact]
    public async Task A_path_that_escapes_the_workspace_is_refused_by_the_client_and_again_by_the_server()
    {
        var source = new WireEditorFileSource(_requester);
        await Assert.ThrowsAsync<ArgumentException>(() => source.SaveAsync(new EditorDocumentId("vault", "../x.md"), "x", "rev", Ct));

        var raw = await Assert.ThrowsAsync<WireRequestException>(() => _requester.RequestAsync<Bazaar.Contracts.Athanor.EditorSaveRequest, Bazaar.Contracts.Athanor.EditorSaveReply>(
            WireTypes.EditorSave, new Bazaar.Contracts.Athanor.EditorSaveRequest("vault", "../x.md", "rev", "x"), Ct));

        Assert.Equal(WireErrorCodes.BadRequest, raw.Error.Code);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "x.md")));
    }

    private sealed class InProcessRequester(IReadOnlyList<IWireRequestHandler> handlers, Bazaar.Server.Wire.Connection.IWireConnection connection) : IWireRequester
    {
        public bool Offline { get; set; }

        public async Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            if (Offline)
            {
                throw new IOException("offline");
            }

            var handler = handlers.Single(h => h.Type == type);
            var request = new Envelope(1, type, EnvelopeKind.Request, "req-1", null, null, null, null, _codec.EncodePayload(payload));
            var reply = await handler.HandleAsync(new WireRequestContext(connection, request), cancellationToken);
            if (!reply.IsOk)
            {
                throw new WireRequestException(reply.Error!);
            }

            return _codec.DecodePayload<TResponse>(new Envelope(1, type, EnvelopeKind.Response, null, "req-1", null, null, null, reply.Payload));
        }
    }

    private sealed class HumanConnection(Guid userId) : Bazaar.Server.Wire.Connection.IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.CreateVersion7();
        public PrincipalRef Principal { get; } = new(userId, "human");
        public string DeviceId => "test";
        public string DeviceClass => "test";
        public IWireCodec Codec => _codec;
        public IReadOnlyList<StreamCursor> HelloCursors => [];
        public Bazaar.Server.Wire.Sync.ISubscriptionSession? Subscriptions => null;
        public CancellationToken Closed => CancellationToken.None;
        public bool TryEnqueueEvent(Envelope envelope) => true;
        public bool TryEnqueueLagged(Lagged lagged) => true;
        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    private sealed class EmptySettings : ISettingStore
    {
        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) => Task.FromResult(key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, System.Text.Json.JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeChecker : IPermissionChecker
    {
        private HashSet<string> _allowed = [];

        public void Allow(params string[] capabilities) => _allowed = [.. capabilities];

        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default) =>
            Task.FromResult(kind == PrincipalKind.Human && _allowed.Contains(capability.Value));
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
