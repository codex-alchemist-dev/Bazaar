using System.Text;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Athanor.Tests;

/// <summary>A path policy refuses before the file is read or written, and the editor opens read-only where it forbids writing.</summary>
public sealed class EditorPathPolicyTests
{
    private const string _ws = "vault";
    private static readonly EditorCaller _alice = new("alice");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryEditorWorkspaceStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly Policy _policy = new();

    public EditorPathPolicyTests()
    {
        _store.AddWorkspace(new EditorWorkspaceInfo(_ws, EditorWorkspaceKind.Vault, ReadOnly: false));
        _store.PutFile(_ws, "notes/a.md", Encoding.UTF8.GetBytes("hello"));
    }

    private EditorOpenHandler Open() => new(_store, new AllowAll(), new AthanorLimits(), _policy);

    private EditorSaveHandler Save()
    {
        var limits = new AthanorLimits();
        return new EditorSaveHandler(_store, new AllowAll(), limits, new SaveRateLimiter(limits, _time), new NoAudit(), _time, _policy);
    }

    [Fact]
    public async Task A_refused_read_is_forbidden_and_even_a_missing_file_is_not_revealed()
    {
        _policy.Deny = true;

        var existing = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), Ct);
        var missing = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/none.md"), Ct);

        Assert.Equal(EditorErrorCodes.Forbidden, existing.ErrorCode);
        Assert.Equal(EditorErrorCodes.Forbidden, missing.ErrorCode);
    }

    [Fact]
    public async Task A_path_that_may_be_read_but_not_written_opens_read_only_and_refuses_a_save()
    {
        _policy.DenyWrite = true;

        var opened = await Open().HandleAsync(_alice, new EditorOpenRequest(_ws, "notes/a.md"), Ct);
        var saved = await Save().HandleAsync(_alice, new EditorSaveRequest(_ws, "notes/a.md", InMemoryEditorWorkspaceStore.RevisionOf(Encoding.UTF8.GetBytes("hello")), "x"), Ct);

        Assert.True(opened.Value!.ReadOnly);
        Assert.Equal(EditorErrorCodes.Forbidden, saved.ErrorCode);
    }

    private sealed class Policy : IEditorPathPolicy
    {
        public bool Deny { get; set; }
        public bool DenyWrite { get; set; }

        public ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, string path, EditorAccess access, CancellationToken cancellationToken) =>
            ValueTask.FromResult(!Deny && !(DenyWrite && access == EditorAccess.Write));
    }

    private sealed class AllowAll : IEditorAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, EditorAccess access, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    private sealed class NoAudit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
