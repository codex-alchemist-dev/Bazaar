using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Athanor;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Athanor;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AthanorOpenReply = Bazaar.Contracts.Athanor.EditorOpenReply;
using AthanorOpenRequest = Bazaar.Contracts.Athanor.EditorOpenRequest;
using AthanorSaveReply = Bazaar.Contracts.Athanor.EditorSaveReply;
using AthanorSaveRequest = Bazaar.Contracts.Athanor.EditorSaveRequest;

namespace Bazaar.Server.Host.Tests;

/// <summary>Host wiring of the Athanor editor (doc 57 section 6): wire adapter, settings-backed limits, registration.</summary>
public sealed class AthanorCompositionTests : IDisposable
{
    private static readonly JsonWireCodec _codec = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "athanor-host-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSettings _settings = new();
    private readonly FakeChecker _checker = new();
    private readonly FakeAudit _audit = new();
    private readonly Guid _userId = Guid.CreateVersion7();

    public AthanorCompositionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        File.WriteAllText(Path.Combine(_root, "notes", "a.md"), "hello");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Athanor:Workspaces:0:Id"] = "vault",
            ["Bazaar:Athanor:Workspaces:0:Kind"] = "Vault",
            ["Bazaar:Athanor:Workspaces:0:Path"] = _root,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ISettingStore>(_settings);
        services.AddSingleton<IPermissionChecker>(_checker);
        services.AddSingleton<IAuditLog>(_audit);
        services.AddBazaarHostAthanor(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static T Handler<T>(IServiceProvider sp) where T : class, IWireRequestHandler =>
        sp.GetServices<IWireRequestHandler>().OfType<T>().Single();

    private static WireRequestContext Request<TPayload>(IWireConnection connection, string type, TPayload payload) =>
        new(connection, new Envelope(1, type, EnvelopeKind.Request, "req-1", null, null, null, null, _codec.EncodePayload(payload)));

    private FakeConnection Human(string kind = "human") => new(new PrincipalRef(_userId, kind));

    private static string Rev(string text) => DirectoryEditorWorkspaceStore.RevisionOf(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void The_host_registers_both_handlers_with_the_wire_types_and_settings_backed_limits()
    {
        using var sp = Build();

        var types = sp.GetServices<IWireRequestHandler>().Select(h => h.Type).ToList();
        Assert.Contains(WireTypes.EditorOpen, types);
        Assert.Contains(WireTypes.EditorSave, types);
        Assert.Contains(WireTypes.EditorCreate, types);
        Assert.IsType<SettingsAthanorLimits>(sp.GetRequiredService<IAthanorLimits>());
        Assert.IsType<DirectoryEditorWorkspaceStore>(sp.GetRequiredService<IEditorWorkspaceStore>());
        Assert.IsType<RolesEditorAuthorizer>(sp.GetRequiredService<IEditorAuthorizer>());
        Assert.Contains(sp.GetServices<ISettingContribution>().SelectMany(c => c.Settings).Select(s => s.Name), n => n == "athanor.save_rate_per_min");
    }

    [Fact]
    public async Task A_configured_vault_repo_becomes_the_vault_workspace_saved_through_the_vault_writer()
    {
        var repo = Path.Combine(_root, "vault-repo");
        Directory.CreateDirectory(repo);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Memory:Vault:RepoPath"] = repo,
            ["Memory:Vault:WriterName"] = "Vault",
            ["Memory:Vault:WriterEmail"] = "vault@example.test",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ISettingStore>(_settings);
        services.AddSingleton<IPermissionChecker>(_checker);
        services.AddSingleton<IAuditLog>(_audit);
        services.AddSingleton<Bazaar.Server.Vault.Git.IGitProvider, Bazaar.Server.Vault.Git.LibGit2GitProvider>();
        services.AddSingleton<Bazaar.Server.Vault.Writes.IVaultWriter>(sp => new Bazaar.Server.Vault.Writes.VaultWriter(
            sp.GetRequiredService<Bazaar.Server.Vault.Git.IGitProvider>(), repo, new Bazaar.Server.Vault.Git.GitIdentity("Vault", "vault@example.test")));
        services.AddBazaarHostAthanor(configuration);
        using var sp = services.BuildServiceProvider(validateScopes: true);

        var store = sp.GetRequiredService<IEditorWorkspaceStore>();
        var info = await store.GetWorkspaceAsync("vault", Ct);

        Assert.IsType<Bazaar.Server.Athanor.Vault.VaultEditorWorkspaceStore>(store);
        Assert.Equal(EditorWorkspaceKind.Vault, info!.Kind);
        Assert.False(info.ReadOnly);
    }

    [Fact]
    public async Task Open_returns_the_file_with_a_revision_for_a_human()
    {
        using var sp = Build();
        _checker.Allow("read", "vault.write");

        var reply = await Handler<EditorOpenWireHandler>(sp).HandleAsync(Request(Human(), WireTypes.EditorOpen, new AthanorOpenRequest("vault", "notes/a.md")), Ct);

        Assert.True(reply.IsOk);
        var body = _codec.DecodePayload<AthanorOpenReply>(new Envelope(1, WireTypes.EditorOpen, EnvelopeKind.Response, null, "req-1", null, null, null, reply.Payload));
        Assert.Equal("hello", body.Content);
        Assert.Equal(Rev("hello"), body.Revision);
        Assert.False(body.ReadOnly);
        Assert.Equal(5, body.SizeBytes);
    }

    [Fact]
    public async Task Open_is_read_only_without_the_write_scope_and_forbidden_without_the_read_scope()
    {
        using var sp = Build();
        var open = Handler<EditorOpenWireHandler>(sp);

        _checker.Allow("read");
        var readOnly = await open.HandleAsync(Request(Human(), WireTypes.EditorOpen, new AthanorOpenRequest("vault", "notes/a.md")), Ct);
        Assert.True(Decode<AthanorOpenReply>(readOnly).ReadOnly);

        _checker.Allow();
        var denied = await open.HandleAsync(Request(Human(), WireTypes.EditorOpen, new AthanorOpenRequest("vault", "notes/a.md")), Ct);
        Assert.False(denied.IsOk);
        Assert.Equal("editor.forbidden", denied.Error!.Code);
    }

    [Fact]
    public async Task Create_over_the_wire_makes_the_file_and_a_second_create_is_editor_exists()
    {
        using var sp = Build();
        _checker.Allow("read", "vault.write");
        var create = Handler<EditorCreateWireHandler>(sp);

        var first = await create.HandleAsync(Request(Human(), WireTypes.EditorCreate, new Bazaar.Contracts.Athanor.EditorCreateRequest("vault", "notes/new.md", "# new")), Ct);
        var second = await create.HandleAsync(Request(Human(), WireTypes.EditorCreate, new Bazaar.Contracts.Athanor.EditorCreateRequest("vault", "notes/new.md", "again")), Ct);
        var agent = await create.HandleAsync(Request(Human("agent"), WireTypes.EditorCreate, new Bazaar.Contracts.Athanor.EditorCreateRequest("vault", "notes/x.md", "x")), Ct);

        Assert.True(first.IsOk);
        Assert.Equal(Rev("# new"), Decode<Bazaar.Contracts.Athanor.EditorCreateReply>(first).Revision);
        Assert.Equal("# new", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "new.md"), Ct));
        Assert.Equal("editor.exists", second.Error!.Code);
        Assert.Equal("editor.forbidden", agent.Error!.Code);
        Assert.False(File.Exists(Path.Combine(_root, "notes", "x.md")));
    }

    [Fact]
    public async Task Save_writes_the_file_returns_the_new_revision_and_audits_it()
    {
        using var sp = Build();
        _checker.Allow("read", "vault.write");

        var reply = await Handler<EditorSaveWireHandler>(sp).HandleAsync(
            Request(Human("owner"), WireTypes.EditorSave, new AthanorSaveRequest("vault", "notes/a.md", Rev("hello"), "changed")), Ct);

        Assert.True(reply.IsOk);
        var body = Decode<AthanorSaveReply>(reply);
        Assert.Equal(Rev("changed"), body.NewRevision);
        Assert.Null(body.ServerContent);
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
        var row = Assert.Single(_audit.Entries);
        Assert.Equal("editor.save", row.Kind);
        Assert.Equal(_userId.ToString("D"), row.Actor);
    }

    [Fact]
    public async Task A_conflicting_save_is_a_res_carrying_the_server_copy_and_no_new_revision()
    {
        using var sp = Build();
        _checker.Allow("read", "vault.write");

        var reply = await Handler<EditorSaveWireHandler>(sp).HandleAsync(
            Request(Human(), WireTypes.EditorSave, new AthanorSaveRequest("vault", "notes/a.md", Rev("older"), "mine")), Ct);

        Assert.True(reply.IsOk);
        var body = Decode<AthanorSaveReply>(reply);
        Assert.Null(body.NewRevision);
        Assert.Equal(Rev("hello"), body.ServerRevision);
        Assert.Equal("hello", body.ServerContent);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Save_without_the_write_scope_is_forbidden_and_traversal_is_a_bad_request()
    {
        using var sp = Build();
        var save = Handler<EditorSaveWireHandler>(sp);
        _checker.Allow("read");

        var denied = await save.HandleAsync(Request(Human(), WireTypes.EditorSave, new AthanorSaveRequest("vault", "notes/a.md", Rev("hello"), "x")), Ct);
        Assert.Equal("editor.forbidden", denied.Error!.Code);

        _checker.Allow("read", "vault.write");
        var bad = await save.HandleAsync(Request(Human(), WireTypes.EditorSave, new AthanorSaveRequest("vault", "../x", Rev("hello"), "x")), Ct);
        Assert.Equal(WireErrorCodes.BadRequest, bad.Error!.Code);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
    }

    [Fact]
    public async Task The_save_rate_limit_comes_from_settings_and_answers_rate_limited_with_retry_after()
    {
        using var sp = Build();
        _checker.Allow("read", "vault.write");
        _settings.Values["athanor.save_rate_per_min"] = 1;
        var save = Handler<EditorSaveWireHandler>(sp);

        var first = await save.HandleAsync(Request(Human(), WireTypes.EditorSave, new AthanorSaveRequest("vault", "notes/a.md", Rev("hello"), "one")), Ct);
        var second = await save.HandleAsync(Request(Human(), WireTypes.EditorSave, new AthanorSaveRequest("vault", "notes/a.md", Rev("one"), "two")), Ct);

        Assert.True(first.IsOk);
        Assert.False(second.IsOk);
        Assert.Equal(WireErrorCodes.RateLimited, second.Error!.Code);
        Assert.True(second.Error.RetryAfterMs > 0);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
    }

    [Fact]
    public async Task The_max_save_bytes_setting_is_enforced()
    {
        using var sp = Build();
        _checker.Allow("read", "vault.write");
        _settings.Values["athanor.max_save_bytes"] = 3;

        var reply = await Handler<EditorSaveWireHandler>(sp).HandleAsync(
            Request(Human(), WireTypes.EditorSave, new AthanorSaveRequest("vault", "notes/a.md", Rev("hello"), "toolong")), Ct);

        Assert.Equal("editor.too_large", reply.Error!.Code);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("service")]
    public async Task Agents_and_services_never_get_the_editor(string kind)
    {
        using var sp = Build();
        _checker.Allow("read", "vault.write");

        var open = await Handler<EditorOpenWireHandler>(sp).HandleAsync(Request(Human(kind), WireTypes.EditorOpen, new AthanorOpenRequest("vault", "notes/a.md")), Ct);
        var save = await Handler<EditorSaveWireHandler>(sp).HandleAsync(
            Request(Human(kind), WireTypes.EditorSave, new AthanorSaveRequest("vault", "notes/a.md", Rev("hello"), "x")), Ct);

        Assert.Equal("editor.forbidden", open.Error!.Code);
        Assert.Equal("editor.forbidden", save.Error!.Code);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
    }

    [Fact]
    public async Task A_payload_that_does_not_fit_is_a_bad_request()
    {
        using var sp = Build();
        var context = new WireRequestContext(Human(), new Envelope(1, WireTypes.EditorOpen, EnvelopeKind.Request, "req-1", null, null, null, null, _codec.EncodePayload("nope")));

        var reply = await Handler<EditorOpenWireHandler>(sp).HandleAsync(context, Ct);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    [Fact]
    public async Task Settings_limits_default_when_unset_pick_up_changes_and_keep_the_last_good_value_on_a_read_failure()
    {
        var limits = new SettingsAthanorLimits(_settings);
        Assert.Equal(AthanorLimits.DefaultMaxBytes, limits.MaxOpenBytes);
        Assert.Equal(AthanorLimits.DefaultSaveRatePerMinute, limits.SaveRatePerMinute);

        _settings.Values["athanor.max_open_bytes"] = 1000;
        _settings.Values["athanor.max_save_bytes"] = 2000;
        _settings.Values["athanor.save_rate_per_min"] = 5;
        await limits.RefreshAsync(Ct);
        Assert.Equal(1000, limits.MaxOpenBytes);
        Assert.Equal(2000, limits.MaxSaveBytes);
        Assert.Equal(5, limits.SaveRatePerMinute);

        _settings.Fail = true;
        await limits.RefreshAsync(Ct);
        Assert.Equal(1000, limits.MaxOpenBytes);
        Assert.Equal(5, limits.SaveRatePerMinute);
    }

    [Fact]
    public void The_athanor_settings_reject_zero_and_default_to_safe_values()
    {
        Assert.Equal(5 * 1024 * 1024, AthanorSettings.MaxOpenBytes.Default);
        Assert.Equal(60, AthanorSettings.SaveRatePerMinute.Default);
        Assert.NotNull(AthanorSettings.MaxSaveBytes.CheckValue(JsonElement.Parse("0")));
        Assert.Null(AthanorSettings.MaxSaveBytes.CheckValue(JsonElement.Parse("1")));
    }

    private static T Decode<T>(WireReply reply) =>
        _codec.DecodePayload<T>(new Envelope(1, "x", EnvelopeKind.Response, null, "req-1", null, null, null, reply.Payload));

    private sealed class FakeConnection(PrincipalRef principal) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.CreateVersion7();
        public PrincipalRef Principal { get; } = principal;
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

    private sealed class FakeSettings : ISettingStore
    {
        public Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);

        public bool Fail { get; set; }

        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("stored value is invalid");
            }

            return Task.FromResult(Values.TryGetValue(key.Name, out var v) ? (T)(object)v : key.Default);
        }

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeChecker : IPermissionChecker
    {
        private HashSet<string> _allowed = [];

        public void Allow(params string[] capabilities) => _allowed = [.. capabilities];

        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default) =>
            Task.FromResult(kind == PrincipalKind.Human && _allowed.Contains(capability.Value));
    }

    private sealed class FakeAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }
}
