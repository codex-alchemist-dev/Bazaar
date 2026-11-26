using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.BrowserPool.Image.Tests;

internal sealed class FakeFileSystem : IPoolImageFileSystem
{
    public Dictionary<string, UnixFileMode> Files { get; } = [];

    public Dictionary<string, UnixFileMode> Directories { get; } = [];

    public Dictionary<string, string> Texts { get; } = [];

    public Dictionary<string, byte[]> Contents { get; } = [];

    public Dictionary<string, long> Lengths { get; } = [];

    public HashSet<string> Throwing { get; } = [];

    public int Calls { get; private set; }

    public bool FileExists(string path)
    {
        Calls++;
        Gate(path);
        return Files.ContainsKey(path) || Texts.ContainsKey(path);
        return Files.ContainsKey(path) || Texts.ContainsKey(path) || Contents.ContainsKey(path);
    }

    public bool DirectoryExists(string path)
    {
        Calls++;
        Gate(path);
        return Directories.ContainsKey(path);
    }

    public UnixFileMode? GetUnixMode(string path)
    {
        Gate(path);
        return Files.TryGetValue(path, out var f) ? f : Directories.TryGetValue(path, out var d) ? d : null;
    }

    public string ReadAllText(string path)
    {
        Gate(path);
        return Texts.TryGetValue(path, out var t) ? t : throw new FileNotFoundException();
    }

    public long GetFileLength(string path)
    {
        Gate(path);
        return Lengths.TryGetValue(path, out var l) ? l : Contents.TryGetValue(path, out var c) ? c.Length : throw new FileNotFoundException();
    }

    public Stream OpenRead(string path)
    {
        Gate(path);
        return Contents.TryGetValue(path, out var c) ? new MemoryStream(c) : throw new FileNotFoundException();
    }

    private void Gate(string path)
    {
        if (Throwing.Contains(path))
        {
            throw new UnauthorizedAccessException("secret detail");
        }
    }

    /// <summary>A pool VM that satisfies the default requirements, with the user namespace restriction on and the profile loaded.</summary>
    public static FakeFileSystem Ready(PoolImageRequirements requirements)
    {
        var fs = new FakeFileSystem();
        const UnixFileMode exe = (UnixFileMode)0b111_101_101; // 0755
        foreach (var r in requirements.Items)
        {
            if (r.Kind == PoolImageRequirementKind.Program)
            {
                string path = r.Path.StartsWith('/') ? r.Path : "/usr/bin/" + r.Path;
                fs.Files[path] = r.SlotsMustExecute ? exe : (UnixFileMode)0b111_000_000;
            }
            else if (r.Kind == PoolImageRequirementKind.Directory)
            {
                fs.Directories[r.Path] = r.SlotsMustExecute ? (UnixFileMode)0b111_001_001 : (UnixFileMode)0b111_000_000;
            }
        }

        fs.Texts[PoolImageReadinessCheck.RestrictionPath] = "1\n";
        fs.Texts[PoolImageReadinessCheck.LoadedProfilesPath] = "bazaar-pool-bwrap (unconfined)\nsnap.x (enforce)\n";
        return fs;
    }
}

public class ReadinessCheckTests
{
    private static readonly PoolImageRequirements _requirements =
        PoolImageRequirements.From(new BrowserPoolOptions(), new LinuxPoolHostOptions { PoolSpawnHelperPath = "/opt/bazaar/bazaar-pool-spawn" });

    private static (PoolImageReadinessCheck Check, FakeFileSystem Fs, FakeTimeProvider Time) Make(Action<FakeFileSystem>? change = null, TimeSpan? interval = null)
    {
        var fs = FakeFileSystem.Ready(_requirements);
        change?.Invoke(fs);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
        var check = new PoolImageReadinessCheck(_requirements, fs, new PoolImageOptions { RecheckInterval = interval ?? TimeSpan.FromMinutes(5) }, time);
        return (check, fs, time);
    }

    private static async Task<IReadOnlyList<PoolReadinessFailure>> Run(PoolImageReadinessCheck check) =>
        await check.CheckAsync(CancellationToken.None);

    [Fact]
    public async Task A_complete_image_passes()
    {
        var (check, _, _) = Make();

        Assert.Empty(await Run(check));
    }

    [Fact]
    public async Task A_missing_program_is_named()
    {
        var (check, _, _) = Make(fs => fs.Files.Remove("/usr/bin/chromium"));

        var failure = Assert.Single(await Run(check));

        Assert.Equal("program:chromium", failure.Id);
        Assert.Contains("/usr/bin/chromium", failure.Reason);
    }

    [Fact]
    public async Task A_program_without_any_execute_bit_fails()
    {
        var (check, _, _) = Make(fs => fs.Files["/usr/bin/setpriv"] = (UnixFileMode)0b110_100_100);

        // The host's probe resolves the bare name "setpriv" to the same file, so it reports too.
        Assert.Contains(await Run(check), f => f.Id == "program:setpriv");
    }

    [Fact]
    public async Task A_slot_program_not_executable_by_others_fails()
    {
        var (check, _, _) = Make(fs => fs.Files["/usr/bin/bwrap"] = (UnixFileMode)0b111_101_100);

        Assert.Equal("program:bwrap", Assert.Single(await Run(check)).Id);
    }

    [Fact]
    public async Task A_program_with_unreadable_permissions_fails_closed()
    {
        var (check, fs, _) = Make();
        fs.Files.Remove("/usr/bin/cage");
        fs.Texts["/usr/bin/cage"] = "x"; // exists, but has no mode

        Assert.Equal("program:cage", Assert.Single(await Run(check)).Id);
    }

    [Fact]
    public async Task Bare_program_names_are_found_on_the_standard_path()
    {
        var (check, fs, _) = Make(f =>
        {
            f.Files.Remove("/usr/bin/nft");
            f.Files["/usr/sbin/nft"] = (UnixFileMode)0b111_101_101;
        });

        Assert.Empty(await Run(check));
        Assert.True(fs.Files.ContainsKey("/usr/sbin/nft"));
    }

    [Fact]
    public async Task A_missing_bare_program_fails()
    {
        var (check, _, _) = Make(f => f.Files.Remove("/usr/bin/systemd-run"));

        Assert.Equal("program:systemd-run", Assert.Single(await Run(check)).Id);
    }

    [Fact]
    public async Task A_missing_directory_fails()
    {
        var (check, _, _) = Make(f => f.Directories.Remove("/var/lib/bazaar-pool"));

        Assert.Equal("directory:slot-root", Assert.Single(await Run(check)).Id);
    }

    [Theory]
    [InlineData(0b111_111_111)] // 0777
    [InlineData(0b111_101_101)] // 0755: slots could list it
    [InlineData(0b111_000_000)] // 0700: slots cannot traverse it
    public async Task A_slot_root_with_the_wrong_mode_fails(int mode)
    {
        var (check, _, _) = Make(f => f.Directories["/var/lib/bazaar-pool"] = (UnixFileMode)mode);

        Assert.Equal("directory:slot-root", Assert.Single(await Run(check)).Id);
    }

    [Fact]
    public async Task A_pipe_parent_open_to_others_fails()
    {
        var (check, _, _) = Make(f => f.Directories["/run/bazaar-pool"] = (UnixFileMode)0b111_001_001);

        Assert.Equal("directory:pipe-parent", Assert.Single(await Run(check)).Id);
    }

    [Fact]
    public async Task Restricted_namespaces_need_the_profile_loaded()
    {
        var (check, _, _) = Make(f => f.Texts[PoolImageReadinessCheck.LoadedProfilesPath] = "snap.x (enforce)\nbazaar-pool-bwrap-other (enforce)\n");

        var failure = Assert.Single(await Run(check));

        Assert.Equal("apparmor:bazaar-pool-bwrap", failure.Id);
    }

    [Fact]
    public async Task Restricted_namespaces_without_the_profile_list_fail()
    {
        var (check, _, _) = Make(f => f.Texts.Remove(PoolImageReadinessCheck.LoadedProfilesPath));

        Assert.Equal("apparmor:bazaar-pool-bwrap", Assert.Single(await Run(check)).Id);
    }

    [Theory]
    [InlineData("0\n")]
    public async Task An_unrestricted_system_needs_no_profile(string value)
    {
        var (check, _, _) = Make(f =>
        {
            f.Texts[PoolImageReadinessCheck.RestrictionPath] = value;
            f.Texts[PoolImageReadinessCheck.LoadedProfilesPath] = string.Empty;
        });

        Assert.Empty(await Run(check));
    }

    [Fact]
    public async Task An_absent_restriction_setting_needs_no_profile()
    {
        var (check, _, _) = Make(f =>
        {
            f.Texts.Remove(PoolImageReadinessCheck.RestrictionPath);
            f.Texts.Remove(PoolImageReadinessCheck.LoadedProfilesPath);
        });

        Assert.Empty(await Run(check));
    }

    [Fact]
    public async Task An_unclear_restriction_value_counts_as_restricted()
    {
        var (check, _, _) = Make(f =>
        {
            f.Texts[PoolImageReadinessCheck.RestrictionPath] = "maybe";
            f.Texts[PoolImageReadinessCheck.LoadedProfilesPath] = string.Empty;
        });

        Assert.Equal("apparmor:bazaar-pool-bwrap", Assert.Single(await Run(check)).Id);
    }

    [Theory]
    [InlineData("/proc/sys/kernel/apparmor_restrict_unprivileged_userns")]
    [InlineData("/sys/kernel/security/apparmor/profiles")]
    [InlineData("/usr/bin/chromium")]
    [InlineData("/var/lib/bazaar-pool")]
    public async Task Read_errors_fail_closed_and_hide_details(string throwing)
    {
        var (check, _, _) = Make(f => f.Throwing.Add(throwing));

        var failures = await Run(check);

        Assert.NotEmpty(failures);
        Assert.All(failures, f => Assert.DoesNotContain("secret detail", f.Reason));
    }

    [Fact]
    public async Task Every_failure_is_reported_in_requirement_order()
    {
        var (check, _, _) = Make(f =>
        {
            f.Files.Remove("/usr/bin/cage");
            f.Directories.Remove("/var/lib/bazaar-pool");
            f.Texts[PoolImageReadinessCheck.LoadedProfilesPath] = string.Empty;
        });

        Assert.Equal(["program:cage", "directory:slot-root", "apparmor:bazaar-pool-bwrap"], (await Run(check)).Select(f => f.Id).ToArray());
    }

    [Fact]
    public async Task A_pass_is_cached_for_the_interval_only()
    {
        var (check, fs, time) = Make();
        Assert.Empty(await Run(check));
        int calls = fs.Calls;

        fs.Files.Remove("/usr/bin/chromium");
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(await Run(check));
        Assert.Equal(calls, fs.Calls);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("program:chromium", Assert.Single(await Run(check)).Id);
    }

    [Fact]
    public async Task A_failure_is_never_cached()
    {
        var (check, fs, _) = Make(f => f.Files.Remove("/usr/bin/chromium"));
        Assert.NotEmpty(await Run(check));

        fs.Files["/usr/bin/chromium"] = (UnixFileMode)0b111_101_101;

        Assert.Empty(await Run(check));
    }

    [Fact]
    public async Task A_failure_after_a_cached_pass_ends_the_cache()
    {
        var (check, fs, time) = Make(interval: TimeSpan.FromSeconds(30));
        Assert.Empty(await Run(check));
        time.Advance(TimeSpan.FromSeconds(31));
        fs.Files.Remove("/usr/bin/chromium");
        Assert.NotEmpty(await Run(check));

        // Still failing straight away: the earlier pass is gone.
        Assert.NotEmpty(await Run(check));
    }

    [Fact]
    public async Task A_cancelled_token_throws()
    {
        var (check, _, _) = Make();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => check.CheckAsync(cts.Token));
    }
}
