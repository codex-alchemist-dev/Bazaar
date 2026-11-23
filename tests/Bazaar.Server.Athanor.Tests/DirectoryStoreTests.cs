using System.Text;

namespace Bazaar.Server.Athanor.Tests;

public sealed class DirectoryStoreTests : IDisposable
{
    private const string _ws = "w";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "athanor-store-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "athanor-out-" + Guid.NewGuid().ToString("N"));
    private readonly AthanorLimits _limits = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DirectoryStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "notes", "a.md"), "hello");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "top secret");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private DirectoryEditorWorkspaceStore Store(bool readOnly = false, EditorWorkspaceKind kind = EditorWorkspaceKind.Vault, IAthanorLimits? limits = null) =>
        new(new ConfiguredEditorWorkspaceResolver(new AthanorWorkspaceOptions
        {
            Workspaces = [new EditorWorkspaceEntry { Id = _ws, Kind = kind, Path = _root, ReadOnly = readOnly }],
        }), limits ?? _limits);

    private static string Rev(string text) => DirectoryEditorWorkspaceStore.RevisionOf(Encoding.UTF8.GetBytes(text));

    private static void Link(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic links cannot be created here: " + e.Message);
        }
    }

    [Fact]
    public async Task Workspace_lookup_returns_info_or_null_and_a_missing_root_is_unavailable()
    {
        var store = Store(readOnly: true, kind: EditorWorkspaceKind.GitWorktree);

        var info = await store.GetWorkspaceAsync(_ws, Ct);
        Assert.Equal(new EditorWorkspaceInfo(_ws, EditorWorkspaceKind.GitWorktree, ReadOnly: true), info);
        Assert.Null(await store.GetWorkspaceAsync("other", Ct));

        var missing = new DirectoryEditorWorkspaceStore(
            new ConfiguredEditorWorkspaceResolver(new AthanorWorkspaceOptions
            {
                Workspaces = [new EditorWorkspaceEntry { Id = "gone", Path = Path.Combine(_root, "nope") }],
            }), _limits);
        await Assert.ThrowsAsync<EditorStoreUnavailableException>(async () => await missing.GetWorkspaceAsync("gone", Ct));
    }

    [Fact]
    public async Task Read_returns_bytes_and_the_sha256_revision()
    {
        var read = await Store().ReadAsync(_ws, "notes/a.md", 100, Ct);

        Assert.Equal(EditorReadStatus.Ok, read.Status);
        Assert.Equal("hello", Encoding.UTF8.GetString(read.Content!));
        Assert.Equal(5, read.SizeBytes);
        Assert.Equal(Rev("hello"), read.Revision);
        Assert.Equal(64, read.Revision!.Length);
    }

    [Fact]
    public async Task Read_of_a_missing_file_or_a_directory_is_not_found()
    {
        var store = Store();
        Assert.Equal(EditorReadStatus.NotFound, (await store.ReadAsync(_ws, "notes/none.md", 100, Ct)).Status);
        Assert.Equal(EditorReadStatus.NotFound, (await store.ReadAsync(_ws, "notes", 100, Ct)).Status);
        Assert.Equal(EditorReadStatus.NotFound, (await store.ReadAsync(_ws, "missing/dir/a.md", 100, Ct)).Status);
    }

    [Fact]
    public async Task Read_over_the_cap_is_too_large_and_exactly_the_cap_is_allowed()
    {
        var store = Store();

        var big = await store.ReadAsync(_ws, "notes/a.md", 4, Ct);
        Assert.Equal(EditorReadStatus.TooLarge, big.Status);
        Assert.Null(big.Content);
        Assert.Equal(5, big.SizeBytes);
        Assert.Equal(EditorReadStatus.Ok, (await store.ReadAsync(_ws, "notes/a.md", 5, Ct)).Status);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("notes/../../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("notes\\..\\..\\x")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("notes/a.md/")]
    [InlineData("notes//a.md")]
    [InlineData("")]
    public async Task Traversal_and_malformed_paths_are_blocked_for_read_and_write(string path)
    {
        var store = Store();

        // Normalised forms (for example "notes//a.md") are refused too, because the store accepts only the normal form.
        var read = await store.ReadAsync(_ws, path, 100, Ct);
        Assert.Equal(EditorReadStatus.Blocked, read.Status);
        var write = await store.WriteAsync(_ws, path, Rev("hello"), "x"u8.ToArray(), Ct);
        Assert.Equal(EditorWriteStatus.Blocked, write.Status);
        Assert.Equal("top secret", await File.ReadAllTextAsync(Path.Combine(_outside, "secret.txt"), Ct));
    }

    [Fact]
    public async Task A_file_symlink_to_outside_the_root_is_blocked()
    {
        Link(Path.Combine(_root, "notes", "leak.md"), Path.Combine(_outside, "secret.txt"), directory: false);
        var store = Store();

        Assert.Equal(EditorReadStatus.Blocked, (await store.ReadAsync(_ws, "notes/leak.md", 100, Ct)).Status);
        var write = await store.WriteAsync(_ws, "notes/leak.md", Rev("top secret"), "pwned"u8.ToArray(), Ct);
        Assert.Equal(EditorWriteStatus.Blocked, write.Status);
        Assert.Equal("top secret", await File.ReadAllTextAsync(Path.Combine(_outside, "secret.txt"), Ct));
    }

    [Fact]
    public async Task A_directory_symlink_to_outside_the_root_is_blocked()
    {
        Link(Path.Combine(_root, "escape"), _outside, directory: true);
        var store = Store();

        Assert.Equal(EditorReadStatus.Blocked, (await store.ReadAsync(_ws, "escape/secret.txt", 100, Ct)).Status);
        var write = await store.WriteAsync(_ws, "escape/secret.txt", Rev("top secret"), "pwned"u8.ToArray(), Ct);
        Assert.Equal(EditorWriteStatus.Blocked, write.Status);
        Assert.Equal("top secret", await File.ReadAllTextAsync(Path.Combine(_outside, "secret.txt"), Ct));
    }

    [Fact]
    public async Task A_symlink_that_stays_inside_the_root_is_also_blocked()
    {
        Link(Path.Combine(_root, "alias.md"), Path.Combine(_root, "notes", "a.md"), directory: false);

        Assert.Equal(EditorReadStatus.Blocked, (await Store().ReadAsync(_ws, "alias.md", 100, Ct)).Status);
    }

    [Fact]
    public async Task A_dangling_symlink_is_blocked_not_not_found()
    {
        Link(Path.Combine(_root, "dangling.md"), Path.Combine(_outside, "does-not-exist"), directory: false);

        Assert.Equal(EditorReadStatus.Blocked, (await Store().ReadAsync(_ws, "dangling.md", 100, Ct)).Status);
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".GIT/hooks/pre-commit")]
    [InlineData("sub/.git")]
    [InlineData("nul")]
    [InlineData("CON.txt")]
    [InlineData("notes/com1.md")]
    public async Task Git_internals_and_windows_device_names_are_blocked(string path)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "[core]");
        var store = Store();

        Assert.Equal(EditorReadStatus.Blocked, (await store.ReadAsync(_ws, path, 100, Ct)).Status);
        Assert.Equal(EditorWriteStatus.Blocked, (await store.WriteAsync(_ws, path, Rev("x"), "x"u8.ToArray(), Ct)).Status);
    }

    [Fact]
    public async Task Write_replaces_the_file_atomically_when_the_revision_matches()
    {
        var store = Store();

        var written = await store.WriteAsync(_ws, "notes/a.md", Rev("hello"), Encoding.UTF8.GetBytes("changed"), Ct);

        Assert.Equal(EditorWriteStatus.Written, written.Status);
        Assert.Equal(Rev("changed"), written.NewRevision);
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "notes"), ".athanor-*"));
        var reread = await store.ReadAsync(_ws, "notes/a.md", 100, Ct);
        Assert.Equal(written.NewRevision, reread.Revision);
    }

    [Fact]
    public async Task Write_with_a_stale_revision_conflicts_and_returns_the_server_copy_without_changing_it()
    {
        var store = Store();

        var result = await store.WriteAsync(_ws, "notes/a.md", Rev("older"), "mine"u8.ToArray(), Ct);

        Assert.Equal(EditorWriteStatus.Conflict, result.Status);
        Assert.Equal(Rev("hello"), result.ServerRevision);
        Assert.Equal("hello", Encoding.UTF8.GetString(result.ServerContent!));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
    }

    [Fact]
    public async Task Write_to_a_missing_file_is_not_found_and_creates_nothing()
    {
        var result = await Store().WriteAsync(_ws, "notes/new.md", Rev(""), "x"u8.ToArray(), Ct);

        Assert.Equal(EditorWriteStatus.NotFound, result.Status);
        Assert.False(File.Exists(Path.Combine(_root, "notes", "new.md")));
    }

    [Fact]
    public async Task A_read_only_workspace_refuses_writes_but_reads()
    {
        var store = Store(readOnly: true);

        Assert.Equal(EditorWriteStatus.Blocked, (await store.WriteAsync(_ws, "notes/a.md", Rev("hello"), "x"u8.ToArray(), Ct)).Status);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
        Assert.Equal(EditorReadStatus.Ok, (await store.ReadAsync(_ws, "notes/a.md", 100, Ct)).Status);
    }

    [Fact]
    public async Task Concurrent_saves_from_the_same_revision_let_exactly_one_win()
    {
        var store = Store();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(
            async () => await store.WriteAsync(_ws, "notes/a.md", Rev("hello"), Encoding.UTF8.GetBytes("writer-" + i), Ct).AsTask(), Ct)));

        var winners = results.Where(r => r.Status == EditorWriteStatus.Written).ToList();
        Assert.Single(winners);
        var losers = results.Where(r => r.Status == EditorWriteStatus.Conflict).ToList();
        Assert.Equal(15, losers.Count);
        var final = await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct);
        Assert.Equal(Rev(final), winners[0].NewRevision);
        Assert.All(losers, l => Assert.Equal(winners[0].NewRevision, l.ServerRevision));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "notes"), ".athanor-*"));
    }

    [Fact]
    public async Task Write_when_the_server_copy_is_over_the_open_limit_is_too_large_and_leaves_it_alone()
    {
        var limits = new AthanorLimits { MaxOpenBytes = 4 };

        var result = await Store(limits: limits).WriteAsync(_ws, "notes/a.md", Rev("hello"), "x"u8.ToArray(), Ct);

        Assert.Equal(EditorWriteStatus.TooLarge, result.Status);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "a.md"), Ct));
    }

    [Fact]
    public async Task Write_keeps_the_unix_file_mode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix modes only.");
            return;
        }

        var target = Path.Combine(_root, "notes", "a.md");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await Store().WriteAsync(_ws, "notes/a.md", Rev("hello"), "x"u8.ToArray(), Ct);

        Assert.Equal(EditorWriteStatus.Written, result.Status);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(target));
    }

    [Fact]
    public async Task An_unknown_workspace_is_unavailable_at_read_and_write()
    {
        var store = Store();

        await Assert.ThrowsAsync<EditorStoreUnavailableException>(async () => await store.ReadAsync("nope", "a.md", 10, Ct));
        await Assert.ThrowsAsync<EditorStoreUnavailableException>(async () => await store.WriteAsync("nope", "a.md", "r", [], Ct));
    }

    [Fact]
    public async Task The_handlers_map_blocked_and_too_large_store_results_to_forbidden_and_too_large()
    {
        var store = Store();
        var auth = new AllowAll();
        var limits = new AthanorLimits();
        var open = new EditorOpenHandler(store, auth, limits);

        var blocked = await open.HandleAsync(new EditorCaller("u"), new EditorOpenRequest(_ws, ".git/config"), Ct);
        Assert.Equal(EditorErrorCodes.Forbidden, blocked.ErrorCode);

        var missing = await open.HandleAsync(new EditorCaller("u"), new EditorOpenRequest(_ws, "notes/none.md"), Ct);
        Assert.Equal(EditorErrorCodes.NotFound, missing.ErrorCode);

        var ok = await open.HandleAsync(new EditorCaller("u"), new EditorOpenRequest(_ws, "notes/a.md"), Ct);
        Assert.True(ok.IsOk);
        Assert.False(ok.Value!.ReadOnly);
        Assert.Equal("markdown", ok.Value.LanguageHint);
    }

    private sealed class AllowAll : IEditorAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(EditorCaller caller, EditorWorkspaceInfo workspace, EditorAccess access, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}
