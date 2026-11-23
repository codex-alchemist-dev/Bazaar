using System.Text;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Athanor.Tests;

public sealed class EditorHandlerTests
{
    private const string _ws = "vault";
    private static readonly EditorCaller _alice = new("alice");

    private readonly InMemoryEditorWorkspaceStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingAudit _audit = new();
    private readonly ScriptedAuthorizer _auth = new();

    public EditorHandlerTests()
    {
        _store.AddWorkspace(new EditorWorkspaceInfo(_ws, EditorWorkspaceKind.Vault, ReadOnly: false));
        _store.PutFile(_ws, "notes/a.md", Encoding.UTF8.GetBytes("hello"));
    }

    private static string Rev(string text) => InMemoryEditorWorkspaceStore.RevisionOf(Encoding.UTF8.GetBytes(text));

    private EditorOpenHandler Open(IAthanorLimits? limits = null) => new(_store, _auth, limits ?? new AthanorLimits());

    private EditorSaveHandler Save(IAthanorLimits? limits = null)
    {
        limits ??= new AthanorLimits();
        return new EditorSaveHandler(_store, _auth, limits, new SaveRateLimiter(limits, _time), _audit, _time);
    }

    private static EditorSaveRequest Req(string content, string baseRevision, string path = "notes/a.md", string ws = _ws) =>
        new(ws, path, baseRevision, content);

    // ---- open ----

    [Fact]
    public async Task Open_returns_content_revision_language_and_size()
    {
        var r = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), TestContext.Current.CancellationToken);

        Assert.True(r.IsOk);
        Assert.Equal("hello", r.Value!.Content);
        Assert.Equal(Rev("hello"), r.Value.Revision);
        Assert.Equal("markdown", r.Value.LanguageHint);
        Assert.False(r.Value.ReadOnly);
        Assert.Equal(5, r.Value.SizeBytes);
    }

    [Fact]
    public async Task Open_is_read_only_without_write_scope()
    {
        _auth.AllowWrite = false;
        var r = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), TestContext.Current.CancellationToken);

        Assert.True(r.IsOk);
        Assert.True(r.Value!.ReadOnly);
    }

    [Fact]
    public async Task Open_is_read_only_in_a_read_only_workspace()
    {
        _store.AddWorkspace(new EditorWorkspaceInfo(_ws, EditorWorkspaceKind.Vault, ReadOnly: true));
        var r = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), TestContext.Current.CancellationToken);

        Assert.True(r.Value!.ReadOnly);
    }

    [Fact]
    public async Task Open_unknown_workspace_and_missing_file_are_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var noWs = await Open().HandleAsync(_alice, new EditorOpenRequest("nope", "a.md"), ct);
        var noFile = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "missing.md"), ct);

        Assert.Equal(EditorErrorCodes.NotFound, noWs.ErrorCode);
        Assert.Equal(EditorErrorCodes.NotFound, noFile.ErrorCode);
    }

    [Fact]
    public async Task Open_without_read_scope_is_forbidden()
    {
        _auth.AllowRead = false;
        var r = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.Forbidden, r.ErrorCode);
    }

    [Fact]
    public async Task Open_over_the_limit_is_too_large()
    {
        var r = await Open(new AthanorLimits { MaxOpenBytes = 4 }).HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.TooLarge, r.ErrorCode);
    }

    [Fact]
    public async Task Open_at_exactly_the_limit_succeeds()
    {
        var r = await Open(new AthanorLimits { MaxOpenBytes = 5 }).HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), TestContext.Current.CancellationToken);

        Assert.True(r.IsOk);
    }

    [Theory]
    [InlineData(new byte[] { 0x68, 0x00, 0x69 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0xFD })]
    public async Task Open_binary_is_refused(byte[] bytes)
    {
        _store.PutFile(_ws, "b.bin", bytes);
        var r = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "b.bin"), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.Binary, r.ErrorCode);
    }

    [Fact]
    public async Task Open_when_store_is_down_is_unavailable()
    {
        _store.Unavailable = true;
        var r = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.Unavailable, r.ErrorCode);
    }

    // ---- paths ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\x")]
    [InlineData("C:\\x.md")]
    [InlineData("a.md:stream")]
    [InlineData("../a.md")]
    [InlineData("notes/../../a.md")]
    [InlineData("notes\\..\\..\\a.md")]
    [InlineData("notes/")]
    [InlineData("notes/a.")]
    [InlineData("a\0b")]
    public async Task Bad_paths_are_bad_request_on_open_and_save(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var open = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, path), ct);
        var save = await Save().HandleAsync(_alice, Req("x", Rev("hello"), path), ct);

        Assert.Equal(EditorErrorCodes.BadRequest, open.ErrorCode);
        Assert.Equal(EditorErrorCodes.BadRequest, save.ErrorCode);
    }

    [Fact]
    public async Task Paths_are_normalised_before_reaching_the_store()
    {
        var r = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, ".\\notes//./a.md"), TestContext.Current.CancellationToken);

        Assert.True(r.IsOk);
    }

    [Fact]
    public void Overlong_path_is_rejected()
    {
        Assert.False(EditorPath.TryNormalize(new string('a', 1025), out _, out _));
    }

    // ---- save ----

    [Fact]
    public async Task Save_writes_returns_new_revision_and_audits()
    {
        var r = await Save().HandleAsync(_alice, Req("hello world", Rev("hello")), TestContext.Current.CancellationToken);

        Assert.True(r.IsOk);
        Assert.Equal(Rev("hello world"), r.Value!.NewRevision);
        Assert.Equal("hello world", Encoding.UTF8.GetString(_store.GetFile(_ws, "notes/a.md")!));
        var row = Assert.Single(_audit.Entries);
        Assert.Equal("alice", row.Actor);
        Assert.Equal(EditorSaveHandler.AuditKind, row.Kind);
        Assert.Equal(_time.GetUtcNow(), row.Ts);
        Assert.Contains("\"workspaceId\":\"vault\"", row.Payload);
        Assert.Contains("\"path\":\"notes/a.md\"", row.Payload);
        Assert.Contains($"\"baseRevision\":\"{Rev("hello")}\"", row.Payload);
        Assert.Contains($"\"newRevision\":\"{Rev("hello world")}\"", row.Payload);
        Assert.Contains("\"bytes\":11", row.Payload);
    }

    [Fact]
    public async Task Save_with_stale_revision_conflicts_with_server_copy_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Save().HandleAsync(_alice, Req("mine", Rev("hello")), ct);
        var stale = await Save().HandleAsync(new EditorCaller("bob"), Req("theirs", Rev("hello")), ct);

        Assert.True(first.IsOk);
        Assert.Equal(EditorErrorCodes.Conflict, stale.ErrorCode);
        Assert.Equal(Rev("mine"), stale.Conflict!.ServerRevision);
        Assert.Equal("mine", stale.Conflict.ServerContent);
        Assert.Equal("mine", Encoding.UTF8.GetString(_store.GetFile(_ws, "notes/a.md")!));
        Assert.Single(_audit.Entries);
    }

    [Fact]
    public async Task Save_to_missing_file_or_workspace_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await Save().HandleAsync(_alice, Req("x", "r", "nope.md"), ct);
        var ws = await Save().HandleAsync(_alice, Req("x", "r", ws: "nope"), ct);

        Assert.Equal(EditorErrorCodes.NotFound, file.ErrorCode);
        Assert.Equal(EditorErrorCodes.NotFound, ws.ErrorCode);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Save_without_write_scope_is_forbidden_and_never_writes()
    {
        _auth.AllowWrite = false;
        var r = await Save().HandleAsync(_alice, Req("x", Rev("hello")), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.Forbidden, r.ErrorCode);
        Assert.Equal("hello", Encoding.UTF8.GetString(_store.GetFile(_ws, "notes/a.md")!));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Authorizer_sees_workspace_kind_and_write_access()
    {
        await Save().HandleAsync(_alice, Req("x", Rev("hello")), TestContext.Current.CancellationToken);

        Assert.Contains((EditorWorkspaceKind.Vault, EditorAccess.Write), _auth.Asked);
    }

    [Fact]
    public async Task Save_to_read_only_workspace_is_refused()
    {
        _store.AddWorkspace(new EditorWorkspaceInfo(_ws, EditorWorkspaceKind.GitWorktree, ReadOnly: true));
        var r = await Save().HandleAsync(_alice, Req("x", Rev("hello")), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.ReadOnlyWorkspace, r.ErrorCode);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Save_over_the_limit_is_too_large_and_at_the_limit_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var limits = new AthanorLimits { MaxSaveBytes = 4 };
        var big = await Save(limits).HandleAsync(_alice, Req("12345", Rev("hello")), ct);
        var ok = await Save(limits).HandleAsync(_alice, Req("1234", Rev("hello")), ct);

        Assert.Equal(EditorErrorCodes.TooLarge, big.ErrorCode);
        Assert.True(ok.IsOk);
    }

    [Fact]
    public async Task Save_limit_counts_utf8_bytes_not_chars()
    {
        var r = await Save(new AthanorLimits { MaxSaveBytes = 3 }).HandleAsync(_alice, Req("\u00e9\u00e9", Rev("hello")), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.TooLarge, r.ErrorCode);
    }

    [Theory]
    [InlineData("a\0b")]
    public async Task Save_binary_content_is_refused(string content)
    {
        var r = await Save().HandleAsync(_alice, Req(content, Rev("hello")), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.Binary, r.ErrorCode);
    }

    [Fact]
    public async Task Save_unpaired_surrogate_is_refused_as_binary()
    {
        var r = await Save().HandleAsync(_alice, Req(new string(['a', (char)0xD800, 'b']), Rev("hello")), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.Binary, r.ErrorCode);
    }

    [Fact]
    public async Task Save_when_store_is_down_is_unavailable()
    {
        _store.Unavailable = true;
        var r = await Save().HandleAsync(_alice, Req("x", Rev("hello")), TestContext.Current.CancellationToken);

        Assert.Equal(EditorErrorCodes.Unavailable, r.ErrorCode);
    }

    [Fact]
    public async Task Save_with_missing_fields_is_bad_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var noRev = await Save().HandleAsync(_alice, Req("x", ""), ct);
        var noWs = await Save().HandleAsync(_alice, Req("x", "r", ws: " "), ct);

        Assert.Equal(EditorErrorCodes.BadRequest, noRev.ErrorCode);
        Assert.Equal(EditorErrorCodes.BadRequest, noWs.ErrorCode);
    }

    // ---- rate limit ----

    [Fact]
    public async Task Save_rate_limit_applies_per_user_and_window_slides()
    {
        var ct = TestContext.Current.CancellationToken;
        var limits = new AthanorLimits { SaveRatePerMinute = 2 };
        var handler = Save(limits);
        var rev = Rev("hello");

        // Conflicting saves still count: the limit protects the server, not the file.
        Assert.Equal(EditorErrorCodes.Conflict, (await handler.HandleAsync(_alice, Req("1", "stale"), ct)).ErrorCode);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await handler.HandleAsync(_alice, Req("2", rev), ct)).IsOk);
        var limited = await handler.HandleAsync(_alice, Req("3", Rev("2")), ct);

        Assert.Equal(EditorErrorCodes.RateLimited, limited.ErrorCode);
        Assert.Equal(50_000, limited.RetryAfterMs);

        // Another user is unaffected.
        Assert.Equal(EditorErrorCodes.Conflict, (await handler.HandleAsync(new EditorCaller("bob"), Req("x", "stale"), ct)).ErrorCode);

        // After the first hit ages out, one slot frees.
        _time.Advance(TimeSpan.FromSeconds(50));
        Assert.True((await handler.HandleAsync(_alice, Req("3", Rev("2")), ct)).IsOk);
        Assert.Equal(EditorErrorCodes.RateLimited, (await handler.HandleAsync(_alice, Req("4", Rev("3")), ct)).ErrorCode);
    }

    [Fact]
    public async Task Default_rate_limit_is_60_per_minute()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = Save();
        var rev = Rev("hello");
        var text = "hello";
        for (var i = 0; i < 60; i++)
        {
            text = "v" + i;
            var r = await handler.HandleAsync(_alice, Req(text, rev), ct);
            Assert.True(r.IsOk);
            rev = r.Value!.NewRevision;
        }

        Assert.Equal(EditorErrorCodes.RateLimited, (await handler.HandleAsync(_alice, Req("late", rev), ct)).ErrorCode);
    }

    // ---- module ----

    [Fact]
    public void Defaults_are_5_mib_and_60_per_minute_and_settings_are_registered()
    {
        var limits = new AthanorLimits();
        Assert.Equal(5L * 1024 * 1024, limits.MaxOpenBytes);
        Assert.Equal(5L * 1024 * 1024, limits.MaxSaveBytes);
        Assert.Equal(60, limits.SaveRatePerMinute);
        Assert.Equal(
            ["athanor.max_open_bytes", "athanor.max_save_bytes", "athanor.save_rate_per_min"],
            AthanorSettings.All.Select(s => s.Name).ToArray());
        Assert.Equal(EditorWireTypes.Open, EditorOpenHandler.Type);
        Assert.Equal(EditorWireTypes.Save, EditorSaveHandler.Type);
    }

    [Fact]
    public void Registration_resolves_handlers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEditorWorkspaceStore>(_store);
        services.AddSingleton<IEditorAuthorizer>(_auth);
        services.AddSingleton<IAuditLog>(_audit);
        services.AddBazaarAthanor();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<EditorOpenHandler>());
        Assert.NotNull(provider.GetRequiredService<EditorSaveHandler>());
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedAuthorizer : IEditorAuthorizer
    {
        public bool AllowRead { get; set; } = true;

        public bool AllowWrite { get; set; } = true;

        public List<(EditorWorkspaceKind, EditorAccess)> Asked { get; } = [];

        public ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, EditorAccess access, CancellationToken cancellationToken)
        {
            Asked.Add((workspace.Kind, access));
            return ValueTask.FromResult(access == EditorAccess.Read ? AllowRead : AllowWrite);
        }
    }
}
