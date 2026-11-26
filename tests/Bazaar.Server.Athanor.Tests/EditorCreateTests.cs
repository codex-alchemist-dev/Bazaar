using System.Text;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Athanor.Tests;

/// <summary>editor.create: the directory store and the handler (doc 57 section 8, PROPOSED).</summary>
public sealed class EditorCreateTests : IDisposable
{
    private const string _ws = "w";
    private static readonly EditorCaller _alice = new("alice");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "athanor-create-" + Guid.NewGuid().ToString("N"));
    private readonly AthanorLimits _limits = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly Audit _audit = new();
    private bool _readOnly;
    private bool _allowWrite = true;

    public EditorCreateTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        File.WriteAllText(Path.Combine(_root, "notes", "a.md"), "hello");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private DirectoryEditorWorkspaceStore Store() =>
        new(new ConfiguredEditorWorkspaceResolver(new AthanorWorkspaceOptions
        {
            Workspaces = [new EditorWorkspaceEntry { Id = _ws, Kind = EditorWorkspaceKind.Vault, Path = _root, ReadOnly = _readOnly }],
        }), _limits);

    private EditorCreateHandler Handler() =>
        new(Store(), new Authorizer(() => _allowWrite), _limits, new SaveRateLimiter(_limits, _time), _audit, _time);

    [Fact]
    public async Task A_new_file_in_an_existing_folder_is_created_with_its_revision()
    {
        var result = await Store().CreateAsync(_ws, "notes/b.md", Encoding.UTF8.GetBytes("# b"), Ct);

        Assert.Equal(EditorCreateStatus.Created, result.Status);
        Assert.Equal(DirectoryEditorWorkspaceStore.RevisionOf(Encoding.UTF8.GetBytes("# b")), result.Revision);
        Assert.Equal("# b", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "b.md"), Ct));
    }

    [Fact]
    public async Task An_existing_file_a_missing_folder_a_git_segment_and_a_read_only_workspace_are_refused()
    {
        Assert.Equal(EditorCreateStatus.Exists, (await Store().CreateAsync(_ws, "notes/a.md", [1], Ct)).Status);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
        Assert.Equal(EditorCreateStatus.ParentMissing, (await Store().CreateAsync(_ws, "nope/b.md", [1], Ct)).Status);
        Assert.False(Directory.Exists(Path.Combine(_root, "nope")));
        Assert.Equal(EditorCreateStatus.Blocked, (await Store().CreateAsync(_ws, ".git/hook", [1], Ct)).Status);
        _readOnly = true;
        Assert.Equal(EditorCreateStatus.Blocked, (await Store().CreateAsync(_ws, "notes/c.md", [1], Ct)).Status);
    }

    [Fact]
    public async Task The_handler_creates_audits_and_maps_each_refusal_to_its_code()
    {
        var ok = await Handler().HandleAsync(_alice, new EditorCreateRequest(_ws, "notes/new.md", "hi"), Ct);
        var exists = await Handler().HandleAsync(_alice, new EditorCreateRequest(_ws, "notes/a.md", "x"), Ct);
        var parent = await Handler().HandleAsync(_alice, new EditorCreateRequest(_ws, "nope/x.md", "x"), Ct);
        var traversal = await Handler().HandleAsync(_alice, new EditorCreateRequest(_ws, "../x.md", "x"), Ct);

        Assert.True(ok.IsOk);
        Assert.Equal(EditorErrorCodes.Exists, exists.ErrorCode);
        Assert.Equal(EditorErrorCodes.NotFound, parent.ErrorCode);
        Assert.Equal(EditorErrorCodes.BadRequest, traversal.ErrorCode);
        var row = Assert.Single(_audit.Entries);
        Assert.Equal(EditorCreateHandler.AuditKind, row.Kind);
    }

    [Fact]
    public async Task Without_the_write_scope_or_over_the_size_cap_nothing_is_created()
    {
        _allowWrite = false;
        var denied = await Handler().HandleAsync(_alice, new EditorCreateRequest(_ws, "notes/new.md", "hi"), Ct);
        _allowWrite = true;
        var big = await Handler().HandleAsync(_alice, new EditorCreateRequest(_ws, "notes/big.md", new string('x', (int)_limits.MaxSaveBytes + 1)), Ct);

        Assert.Equal(EditorErrorCodes.Forbidden, denied.ErrorCode);
        Assert.Equal(EditorErrorCodes.TooLarge, big.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "notes", "new.md")));
        Assert.False(File.Exists(Path.Combine(_root, "notes", "big.md")));
    }

    private sealed class Authorizer(Func<bool> write) : IEditorAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, EditorAccess access, CancellationToken cancellationToken) =>
            ValueTask.FromResult(access == EditorAccess.Read || write());
    }

    private sealed class Audit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }
}
