using System.Text;

namespace Bazaar.ModulePermissions.Fs.Tests;

public sealed class ModuleFileSystemTests : IDisposable
{
    private const string _module = "bazaar.notes";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "bazaar-fs-" + Guid.NewGuid().ToString("N"));
    private readonly string _local;
    private readonly string _cache;
    private readonly string _outside;

    public ModuleFileSystemTests()
    {
        _local = Path.Combine(_base, "local");
        _cache = Path.Combine(_base, "cache");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_local);
        Directory.CreateDirectory(_cache);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ListSink : IModulePermissionDenialSink
    {
        public List<ModulePermissionDenial> Denials { get; } = [];

        public void Record(ModulePermissionDenial denial) => Denials.Add(denial);
    }

    private (ModuleFileSystem Fs, ListSink Sink) Make(string[] permissions, ModuleFsOptions? options = null, bool activate = true)
    {
        var sink = new ListSink();
        var gate = new ModulePermissionGate(sink, TimeProvider.System);
        if (activate)
        {
            gate.Activate(_module, permissions);
        }

        var roots = new ModuleFsRoots(new Dictionary<string, string> { ["local"] = _local, ["cache"] = _cache });
        return (new ModuleFileSystem(gate, _module, roots, options), sink);
    }

    private ModuleFileSystem Granted(ModuleFsOptions? options = null) => Make(["fs:local/*", "fs:cache/*"], options).Fs;

    private static void MakeLinkOrSkip(string link, string target, bool directory)
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("The OS refused to create a symbolic link.");
        }
    }

    // Happy paths

    [Fact]
    public async Task A_granted_file_can_be_written_read_listed_and_deleted()
    {
        var fs = Granted();
        fs.CreateDirectory("local/notes");

        await fs.WriteAllBytesAsync("local/notes/a.txt", Encoding.UTF8.GetBytes("hello"), Ct);

        Assert.True(fs.Exists("local/notes/a.txt"));
        Assert.Equal("hello", Encoding.UTF8.GetString(await fs.ReadAllBytesAsync("local/notes/a.txt", Ct)));
        Assert.Equal(["a.txt"], fs.Enumerate("local/notes"));
        fs.Delete("local/notes/a.txt");
        Assert.False(fs.Exists("local/notes/a.txt"));
        Assert.True(File.Exists(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public async Task Streams_write_append_and_read()
    {
        var fs = Granted();
        await using (var w = fs.OpenWrite("local/s.txt"))
        {
            await w.WriteAsync("ab"u8.ToArray(), Ct);
        }

        await using (var a = fs.OpenAppend("local/s.txt"))
        {
            await a.WriteAsync("cd"u8.ToArray(), Ct);
        }

        await using var r = fs.OpenRead("local/s.txt");
        using var reader = new StreamReader(r);
        Assert.Equal("abcd", await reader.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task Move_inside_one_root_works()
    {
        var fs = Granted();
        await fs.WriteAllBytesAsync("local/a.txt", new byte[] { 1, 2 }, Ct);

        fs.Move("local/a.txt", "local/b.txt");

        Assert.False(fs.Exists("local/a.txt"));
        Assert.True(fs.Exists("local/b.txt"));
    }

    [Fact]
    public void Enumerate_of_a_root_needs_a_grant_of_the_root_itself()
    {
        var (fs, _) = Make(["fs:local/*"]);
        Assert.Throws<ModulePermissionDeniedException>(() => fs.Enumerate("local"));

        var (fs2, _) = Make(["fs:local"]);
        Assert.Empty(fs2.Enumerate("local"));
    }

    // Gate

    [Fact]
    public async Task An_ungranted_path_is_refused_and_audited()
    {
        var (fs, sink) = Make(["fs:local/notes/*"]);

        await Assert.ThrowsAsync<ModulePermissionDeniedException>(async () => await fs.ReadAllBytesAsync("local/other/a.txt", Ct));

        Assert.Single(sink.Denials);
    }

    [Fact]
    public void A_prefix_grant_covers_below_but_not_the_prefix_and_not_a_sibling_with_the_same_text()
    {
        var (fs, _) = Make(["fs:local/data/*"]);
        Directory.CreateDirectory(Path.Combine(_local, "data"));
        Directory.CreateDirectory(Path.Combine(_local, "data2"));

        Assert.False(fs.Exists("local/data/x"));
        Assert.Throws<ModulePermissionDeniedException>(() => fs.Exists("local/data"));
        Assert.Throws<ModulePermissionDeniedException>(() => fs.Exists("local/data2/x"));
        Assert.Throws<ModulePermissionDeniedException>(() => fs.Exists("local/datax"));
    }

    [Fact]
    public void A_module_that_is_not_running_is_refused()
    {
        var (fs, sink) = Make(["fs:local/*"], activate: false);

        Assert.Throws<ModulePermissionDeniedException>(() => fs.Exists("local/a"));

        Assert.Single(sink.Denials);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, sink.Denials[0].Decision);
    }

    [Fact]
    public void An_unknown_root_is_refused_even_when_granted()
    {
        var (fs, _) = Make(["fs:vault/*"]);
        Assert.Throws<ModuleFsRefusedException>(() => fs.Exists("vault/a"));
    }

    [Fact]
    public void A_missing_root_directory_is_refused()
    {
        Directory.Delete(_cache);
        var fs = Granted();
        Assert.Throws<ModuleFsRefusedException>(() => fs.Exists("cache/a"));
    }

    // Lexical attacks

    [Theory]
    [InlineData("local/../outside/secret.txt")]
    [InlineData("local/a/../../outside/secret.txt")]
    [InlineData("local/./a")]
    [InlineData("local//a")]
    [InlineData("local/a/")]
    [InlineData("/local/a")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("//server/share/x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("local\\a")]
    [InlineData("local/..\\outside")]
    [InlineData("local/a*")]
    [InlineData("local/*")]
    [InlineData("")]
    [InlineData("local")]
    public void Malformed_or_escaping_paths_are_refused(string path)
    {
        var fs = Granted();

        var e = Record.Exception(() => fs.Exists(path));

        Assert.True(e is UnauthorizedAccessException, "Expected a refusal.");
        // The outside file must be untouched whatever the path was.
        Assert.True(File.Exists(Path.Combine(_outside, "secret.txt")));
    }

    [Theory]
    [InlineData("local/%2e%2e%2foutside")]
    [InlineData("local/..%2foutside")]
    public async Task Encoded_traversal_is_only_plain_text_and_names_a_file_inside_the_root(string path)
    {
        var fs = Granted();

        await fs.WriteAllBytesAsync(path, new byte[] { 1 }, Ct);

        Assert.Single(Directory.GetFiles(_outside));
        Assert.Single(Directory.GetFiles(_local));
    }

    [Theory]
    [InlineData("local/CON")]
    [InlineData("local/nul")]
    [InlineData("local/NUL.txt")]
    [InlineData("local/COM1")]
    [InlineData("local/lpt1.log")]
    [InlineData("local/aux.")]
    [InlineData("local/file.")]
    [InlineData("local/file ")]
    [InlineData("local/file.txt ")]
    [InlineData("local/a:b")]
    [InlineData("local/a.txt:stream")]
    [InlineData("local/a?b")]
    [InlineData("local/a<b")]
    [InlineData("local/a|b")]
    [InlineData("local/a\"b")]
    [InlineData("local/a\0b")]
    [InlineData("local/a\nb")]
    [InlineData("local/a\tb")]
    [InlineData("local/a\u202Eb")]
    public void Unsafe_file_names_are_refused_and_the_message_does_not_echo_the_input(string path)
    {
        var (fs, _) = Make(["fs:local/*"]);

        var e = Assert.ThrowsAny<UnauthorizedAccessException>(() => fs.Exists(path));

        Assert.DoesNotContain("local/", e.Message, StringComparison.Ordinal);
        Assert.All(e.Message, c => Assert.False(char.IsControl(c)));
    }

    [Fact]
    public void An_over_long_path_and_too_many_segments_are_refused()
    {
        var fs = Granted(new ModuleFsOptions(MaxPathLength: 40, MaxSegments: 4));

        Assert.Throws<ModuleFsRefusedException>(() => fs.Exists("local/" + new string('a', 60)));
        Assert.Throws<ModuleFsRefusedException>(() => fs.Exists("local/a/b/c/d"));
        Assert.False(fs.Exists("local/a/b/c"));
    }

    [Fact]
    public void A_malformed_path_is_audited_without_its_text()
    {
        var (fs, sink) = Make(["fs:local/*"]);

        Assert.Throws<ModulePermissionDeniedException>(() => fs.Exists("local/../x"));

        Assert.Single(sink.Denials);
        Assert.Null(sink.Denials[0].Target);
    }

    // Links

    [Fact]
    public void A_directory_link_in_the_middle_pointing_outside_is_refused()
    {
        MakeLinkOrSkip(Path.Combine(_local, "jump"), _outside, directory: true);
        var fs = Granted();

        Assert.Throws<ModuleFsRefusedException>(() => fs.OpenRead("local/jump/secret.txt"));
        Assert.Throws<ModuleFsRefusedException>(() => fs.Exists("local/jump/secret.txt"));
        Assert.Throws<ModuleFsRefusedException>(() => fs.OpenWrite("local/jump/new.txt"));
        Assert.Throws<ModuleFsRefusedException>(() => fs.CreateDirectory("local/jump/sub"));
        Assert.False(File.Exists(Path.Combine(_outside, "new.txt")));
    }

    [Fact]
    public async Task A_file_link_at_the_leaf_pointing_outside_is_refused()
    {
        MakeLinkOrSkip(Path.Combine(_local, "leaf.txt"), Path.Combine(_outside, "secret.txt"), directory: false);
        var fs = Granted();

        Assert.Throws<ModuleFsRefusedException>(() => fs.OpenRead("local/leaf.txt"));
        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await fs.WriteAllBytesAsync("local/leaf.txt", new byte[] { 1 }, Ct));
        Assert.Throws<ModuleFsRefusedException>(() => fs.Delete("local/leaf.txt"));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public void A_dangling_link_is_refused_for_writing()
    {
        var target = Path.Combine(_outside, "not-there.txt");
        MakeLinkOrSkip(Path.Combine(_local, "dangling.txt"), target, directory: false);
        var fs = Granted();

        Assert.Throws<ModuleFsRefusedException>(() => fs.OpenWrite("local/dangling.txt"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void A_link_inside_the_root_to_another_place_inside_the_root_is_still_refused()
    {
        Directory.CreateDirectory(Path.Combine(_local, "real"));
        MakeLinkOrSkip(Path.Combine(_local, "alias"), Path.Combine(_local, "real"), directory: true);
        var fs = Granted();

        Assert.Throws<ModuleFsRefusedException>(() => fs.Exists("local/alias/x"));
    }

    [Fact]
    public void Enumerate_does_not_return_links()
    {
        MakeLinkOrSkip(Path.Combine(_local, "jump"), _outside, directory: true);
        MakeLinkOrSkip(Path.Combine(_local, "leaf.txt"), Path.Combine(_outside, "secret.txt"), directory: false);
        File.WriteAllText(Path.Combine(_local, "real.txt"), "x");
        var (fs, _) = Make(["fs:local"]);

        Assert.Equal(["real.txt"], fs.Enumerate("local"));
    }

    [Fact]
    public void A_root_that_is_itself_a_link_is_the_operators_choice_and_works()
    {
        var linkRoot = Path.Combine(_base, "linked");
        MakeLinkOrSkip(linkRoot, _local, directory: true);
        var gate = new ModulePermissionGate(new ListSink(), TimeProvider.System);
        gate.Activate(_module, ["fs:linked/*"]);
        var fs = new ModuleFileSystem(gate, _module, new ModuleFsRoots(new Dictionary<string, string> { ["linked"] = linkRoot }));

        fs.CreateDirectory("linked/d");

        Assert.True(Directory.Exists(Path.Combine(_local, "d")));
    }

    // Limits

    [Fact]
    public async Task Size_limits_are_enforced_for_reads_and_writes()
    {
        var fs = Granted(new ModuleFsOptions(MaxFileBytes: 4));

        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await fs.WriteAllBytesAsync("local/big", new byte[5], Ct));
        Assert.False(fs.Exists("local/big"));
        File.WriteAllBytes(Path.Combine(_local, "big"), new byte[5]);
        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await fs.ReadAllBytesAsync("local/big", Ct));
        await fs.WriteAllBytesAsync("local/ok", new byte[4], Ct);
        Assert.Equal(4, (await fs.ReadAllBytesAsync("local/ok", Ct)).Length);
    }

    [Fact]
    public void Enumerate_over_the_entry_limit_is_refused_not_truncated()
    {
        File.WriteAllText(Path.Combine(_local, "1"), "");
        File.WriteAllText(Path.Combine(_local, "2"), "");
        File.WriteAllText(Path.Combine(_local, "3"), "");
        var (fs, _) = Make(["fs:local"], new ModuleFsOptions(MaxEnumerateEntries: 2));

        Assert.Throws<ModuleFsRefusedException>(() => fs.Enumerate("local"));
    }

    [Fact]
    public void A_missing_file_is_refused_without_the_host_path()
    {
        var fs = Granted();

        var e = Assert.Throws<ModuleFsRefusedException>(() => fs.OpenRead("local/missing.txt"));

        Assert.DoesNotContain(_base, e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public void A_directory_cannot_be_opened_or_deleted_as_a_file()
    {
        Directory.CreateDirectory(Path.Combine(_local, "d"));
        var fs = Granted();

        Assert.Throws<ModuleFsRefusedException>(() => fs.OpenRead("local/d"));
        Assert.Throws<ModuleFsRefusedException>(() => fs.Delete("local/d"));
    }

    // Move

    [Fact]
    public async Task Move_across_roots_is_refused()
    {
        var fs = Granted();
        await fs.WriteAllBytesAsync("local/a.txt", new byte[] { 1 }, Ct);

        Assert.Throws<ModuleFsRefusedException>(() => fs.Move("local/a.txt", "cache/a.txt"));

        Assert.True(fs.Exists("local/a.txt"));
        Assert.False(fs.Exists("cache/a.txt"));
    }

    [Fact]
    public async Task Move_to_an_ungranted_destination_is_refused()
    {
        var (fs, sink) = Make(["fs:local/src/*", "fs:local/dst/ok.txt"]);
        Directory.CreateDirectory(Path.Combine(_local, "src"));
        Directory.CreateDirectory(Path.Combine(_local, "dst"));
        await fs.WriteAllBytesAsync("local/src/a.txt", new byte[] { 1 }, Ct);

        Assert.Throws<ModulePermissionDeniedException>(() => fs.Move("local/src/a.txt", "local/dst/other.txt"));

        Assert.True(File.Exists(Path.Combine(_local, "src", "a.txt")));
        Assert.Single(sink.Denials);
        fs.Move("local/src/a.txt", "local/dst/ok.txt");
        Assert.True(File.Exists(Path.Combine(_local, "dst", "ok.txt")));
    }

    [Fact]
    public async Task Move_does_not_overwrite_and_refuses_an_escaping_destination()
    {
        var fs = Granted();
        await fs.WriteAllBytesAsync("local/a.txt", new byte[] { 1 }, Ct);
        await fs.WriteAllBytesAsync("local/b.txt", new byte[] { 2 }, Ct);

        Assert.Throws<ModuleFsRefusedException>(() => fs.Move("local/a.txt", "local/b.txt"));
        Assert.ThrowsAny<UnauthorizedAccessException>(() => fs.Move("local/a.txt", "local/../outside/a.txt"));
        Assert.Equal(new byte[] { 2 }, await fs.ReadAllBytesAsync("local/b.txt", Ct));
    }

    // Size limits on streams and roots

    private static ModuleFsOptions Small(long file = 10, long root = 25) => new(MaxFileBytes: file, MaxRootBytes: root);

    private long LocalLength(string name) => new FileInfo(Path.Combine(_local, name)).Length;

    [Fact]
    public void Sync_writes_past_the_file_limit_are_refused_and_nothing_is_written()
    {
        var fs = Granted(Small());
        using var w = fs.OpenWrite("local/a.bin");
        w.Write(new byte[6], 0, 6);

        Assert.Throws<ModuleFsRefusedException>(() => w.Write(new byte[5], 0, 5));
        Assert.Throws<ModuleFsRefusedException>(() => w.Write(new byte[5].AsSpan()));
        w.Write(new byte[4]);
        w.Flush();

        Assert.Equal(10, LocalLength("a.bin"));
        Assert.Throws<ModuleFsRefusedException>(() => w.WriteByte(1));
        Assert.Equal(10, w.Length);
    }

    [Fact]
    public async Task Async_writes_past_the_file_limit_are_refused()
    {
        var fs = Granted(Small());
        await using var w = fs.OpenWrite("local/a.bin");
        await w.WriteAsync(new byte[8], Ct);

        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await w.WriteAsync(new byte[3], Ct));
#pragma warning disable CA1835 // The array overload is the one under test.
        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await w.WriteAsync(new byte[3], 0, 3, Ct));
#pragma warning restore CA1835
        await w.FlushAsync(Ct);

        Assert.Equal(8, LocalLength("a.bin"));
    }

    [Fact]
    public void WriteByte_stops_at_the_file_limit()
    {
        var fs = Granted(Small());
        using (var w = fs.OpenWrite("local/a.bin"))
        {
            for (var i = 0; i < 10; i++)
            {
                w.WriteByte(1);
            }

            Assert.Throws<ModuleFsRefusedException>(() => w.WriteByte(1));
        }

        Assert.Equal(10, LocalLength("a.bin"));
    }

    [Fact]
    public void SetLength_past_the_file_limit_is_refused_and_a_shorter_length_frees_quota()
    {
        var fs = Granted(Small(file: 10, root: 12));
        using (var w = fs.OpenWrite("local/a.bin"))
        {
            w.SetLength(10);
            Assert.Throws<ModuleFsRefusedException>(() => w.SetLength(11));
            w.SetLength(2);
        }

        Assert.Equal(2, LocalLength("a.bin"));
        using var b = fs.OpenWrite("local/b.bin");
        b.SetLength(10);
        Assert.Equal(10, LocalLength("b.bin"));
    }

    [Fact]
    public void A_seek_past_the_end_then_a_write_that_passes_the_limit_is_refused()
    {
        var fs = Granted(Small());
        using (var w = fs.OpenWrite("local/a.bin"))
        {
            w.Seek(8, SeekOrigin.Begin);
            Assert.Throws<ModuleFsRefusedException>(() => w.Write(new byte[3]));
            w.Write(new byte[2]);
        }

        Assert.Equal(10, LocalLength("a.bin"));
    }

    [Fact]
    public void Append_counts_the_existing_length()
    {
        var fs = Granted(Small());
        File.WriteAllBytes(Path.Combine(_local, "a.bin"), new byte[8]);

        using (var a = fs.OpenAppend("local/a.bin"))
        {
            Assert.Throws<ModuleFsRefusedException>(() => a.Write(new byte[3]));
            a.Write(new byte[2]);
        }

        Assert.Equal(10, LocalLength("a.bin"));
    }

    [Fact]
    public void The_root_quota_is_refused_across_two_files()
    {
        var fs = Granted(Small(file: 10, root: 15));
        using (var a = fs.OpenWrite("local/a.bin"))
        {
            a.Write(new byte[10]);
        }

        using (var b = fs.OpenWrite("local/b.bin"))
        {
            b.Write(new byte[5]);
            Assert.Throws<ModuleFsRefusedException>(() => b.WriteByte(1));
        }

        Assert.Equal(5, LocalLength("b.bin"));
        // The other root has its own quota.
        using var c = fs.OpenWrite("cache/c.bin");
        c.Write(new byte[10]);
    }

    [Fact]
    public async Task A_whole_file_write_that_would_pass_the_quota_leaves_the_old_file_alone()
    {
        var fs = Granted(Small(file: 10, root: 12));
        await fs.WriteAllBytesAsync("local/a.bin", new byte[] { 1, 2 }, Ct);
        await fs.WriteAllBytesAsync("local/b.bin", new byte[10], Ct);

        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await fs.WriteAllBytesAsync("local/a.bin", new byte[5], Ct));

        Assert.Equal(new byte[] { 1, 2 }, await fs.ReadAllBytesAsync("local/a.bin", Ct));
    }

    [Fact]
    public async Task Deleting_a_file_frees_quota()
    {
        var fs = Granted(Small(file: 10, root: 12));
        await fs.WriteAllBytesAsync("local/a.bin", new byte[10], Ct);
        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await fs.WriteAllBytesAsync("local/b.bin", new byte[5], Ct));

        fs.Delete("local/a.bin");
        fs.Delete("local/missing.bin");

        await fs.WriteAllBytesAsync("local/b.bin", new byte[10], Ct);
    }

    [Fact]
    public async Task Overwriting_a_file_with_smaller_content_frees_quota()
    {
        var fs = Granted(Small(file: 10, root: 15));
        await fs.WriteAllBytesAsync("local/a.bin", new byte[10], Ct);
        await fs.WriteAllBytesAsync("local/a.bin", new byte[2], Ct);
        await fs.WriteAllBytesAsync("local/b.bin", new byte[10], Ct);
        await using (var reopened = fs.OpenWrite("local/b.bin"))
        {
            // Opening for write truncates, which gives the old content back too.
        }

        await fs.WriteAllBytesAsync("local/c.bin", new byte[10], Ct);
        await fs.WriteAllBytesAsync("local/d.bin", new byte[3], Ct);
        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await fs.WriteAllBytesAsync("local/e.bin", new byte[1], Ct));
    }

    [Fact]
    public async Task Files_already_in_the_root_count_on_the_first_write()
    {
        Directory.CreateDirectory(Path.Combine(_local, "sub"));
        File.WriteAllBytes(Path.Combine(_local, "sub", "old.bin"), new byte[12]);
        var fs = Granted(Small(file: 10, root: 15));

        await Assert.ThrowsAsync<ModuleFsRefusedException>(async () => await fs.WriteAllBytesAsync("local/a.bin", new byte[4], Ct));
        await fs.WriteAllBytesAsync("local/a.bin", new byte[3], Ct);
    }

    [Fact]
    public async Task Concurrent_writers_on_different_files_never_pass_the_quota_together()
    {
        var fs = Granted(Small(file: 10, root: 25));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            try
            {
                await using var w = fs.OpenWrite($"local/f{i}.bin");
                for (var n = 0; n < 10; n++)
                {
                    await w.WriteAsync(new byte[1], Ct);
                }

                return true;
            }
            catch (ModuleFsRefusedException)
            {
                return false;
            }
        }, Ct)));

        var total = Directory.GetFiles(_local).Sum(f => new FileInfo(f).Length);
        Assert.True(total <= 25, $"The root held {total} bytes.");
        Assert.True(results.Any(r => r) || total > 0);
    }

    [Fact]
    public void The_root_limit_must_not_be_smaller_than_the_file_limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Granted(new ModuleFsOptions(MaxFileBytes: 10, MaxRootBytes: 9)));
        Assert.Equal(1024L * 1024 * 1024, new ModuleFsOptions().MaxRootBytes);
    }

    [Fact]
    public async Task Links_under_the_root_are_not_counted_or_followed_by_the_usage_walk()
    {
        File.WriteAllBytes(Path.Combine(_outside, "big.bin"), new byte[50]);
        MakeLinkOrSkip(Path.Combine(_local, "dir-link"), _outside, directory: true);
        MakeLinkOrSkip(Path.Combine(_local, "file-link"), Path.Combine(_outside, "big.bin"), directory: false);
        var fs = Granted(Small(file: 10, root: 15));

        await fs.WriteAllBytesAsync("local/a.bin", new byte[10], Ct);

        Assert.True(File.Exists(Path.Combine(_outside, "big.bin")));
    }

    // Roots

    [Fact]
    public void Roots_reject_bad_names_relative_directories_and_duplicates()
    {
        Assert.Throws<ArgumentException>(() => new ModuleFsRoots(new Dictionary<string, string> { ["a/b"] = _local }));
        Assert.Throws<ArgumentException>(() => new ModuleFsRoots(new Dictionary<string, string> { [".."] = _local }));
        Assert.Throws<ArgumentException>(() => new ModuleFsRoots(new Dictionary<string, string> { ["local"] = "relative/dir" }));
        Assert.Throws<ArgumentException>(() => new ModuleFsRoots([new("local", _local), new("local", _cache)]));
    }

    [Fact]
    public async Task A_directory_move_cannot_carry_files_the_module_has_no_grant_for()
    {
        Directory.CreateDirectory(Path.Combine(_local, "shared", "deep"));
        File.WriteAllText(Path.Combine(_local, "shared", "deep", "note.txt"), "not yours");
        Directory.CreateDirectory(Path.Combine(_local, "mine"));
        var (fs, _) = Make(["fs:local/shared", "fs:local/mine/*"]);

        Assert.Throws<ModuleFsRefusedException>(() => fs.Move("local/shared", "local/mine/taken"));

        Assert.True(File.Exists(Path.Combine(_local, "shared", "deep", "note.txt")));
        Assert.False(Directory.Exists(Path.Combine(_local, "mine", "taken")));
        await Assert.ThrowsAsync<ModulePermissionDeniedException>(async () => await fs.ReadAllBytesAsync("local/shared/deep/note.txt", Ct));
    }

    [Fact]
    public void CreateDirectory_does_not_create_the_directories_above_a_grant()
    {
        var (fs, _) = Make(["fs:local/a/b/*"]);

        Assert.Throws<ModuleFsRefusedException>(() => fs.CreateDirectory("local/a/b/c"));
        Assert.False(Directory.Exists(Path.Combine(_local, "a")));

        Directory.CreateDirectory(Path.Combine(_local, "a", "b"));
        fs.CreateDirectory("local/a/b/c");
        Assert.True(Directory.Exists(Path.Combine(_local, "a", "b", "c")));
    }

    [Theory]
    [InlineData("local/a:stream")]
    [InlineData("local/CON")]
    [InlineData("local/x.")]
    [InlineData("/local/x")]
    [InlineData("local/x/")]
    [InlineData("local")]
    public void A_path_refused_by_a_file_name_rule_is_still_audited_when_it_is_not_granted(string path)
    {
        var (fs, sink) = Make(["fs:cache/*"]);

        Assert.ThrowsAny<UnauthorizedAccessException>(() => fs.Exists(path));

        Assert.Single(sink.Denials);
    }

    [Fact]
    public void An_over_long_or_over_deep_path_is_audited_when_it_is_not_granted()
    {
        var (fs, sink) = Make(["fs:cache/*"], new ModuleFsOptions(MaxPathLength: 16, MaxSegments: 3));

        Assert.ThrowsAny<UnauthorizedAccessException>(() => fs.Exists("local/" + new string('a', 20)));
        Assert.ThrowsAny<UnauthorizedAccessException>(() => fs.Exists("local/a/b/c/d"));

        Assert.Equal(2, sink.Denials.Count);
    }

    [Fact]
    public void A_cross_root_move_is_audited_once_for_each_end_that_is_not_granted()
    {
        var (fs, sink) = Make(["fs:local/*"]);
        File.WriteAllText(Path.Combine(_local, "a.txt"), "a");

        Assert.Throws<ModulePermissionDeniedException>(() => fs.Move("local/a.txt", "cache/a.txt"));

        Assert.Single(sink.Denials);
        Assert.True(File.Exists(Path.Combine(_local, "a.txt")));
    }

    [Fact]
    public void Roots_that_are_a_filesystem_root_or_contain_one_another_are_refused()
    {
        var fsRoot = Path.GetPathRoot(_local)!;
        Assert.Throws<ArgumentException>(() => new ModuleFsRoots(new Dictionary<string, string> { ["sys"] = fsRoot }));
        Assert.Throws<ArgumentException>(() => new ModuleFsRoots([new("local", _base), new("cache", _cache)]));
        Assert.Throws<ArgumentException>(() => new ModuleFsRoots([new("cache", _cache), new("local", _base)]));
        Assert.Throws<ArgumentException>(() => new ModuleFsRoots([new("one", _local), new("two", _local + Path.DirectorySeparatorChar)]));

        // A sibling whose name only starts with the other's name is not inside it.
        _ = new ModuleFsRoots([new("local", _local), new("other", _local + "-2")]);
    }

    [Fact]
    public void A_whole_file_limit_larger_than_an_array_is_refused()
    {
        var sink = new ListSink();
        var gate = new ModulePermissionGate(sink, TimeProvider.System);
        var roots = new ModuleFsRoots(new Dictionary<string, string> { ["local"] = _local });
        var options = new ModuleFsOptions(MaxFileBytes: (long)Array.MaxLength + 1, MaxRootBytes: long.MaxValue);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ModuleFileSystem(gate, _module, roots, options));
    }
}
