using Bazaar.Server.Components.Backup.Offsite;
using Bazaar.Server.Components.Backup.Processes;

namespace Bazaar.Server.Components.Backup.Tests;

/// <summary>Cookie profile exclusion (doc 33, "Cookie profile exclusion [PROPOSED]"), for both values of the flag.</summary>
public sealed class CookieProfileExclusionTests
{
    private static readonly string[] _cookieLikePaths =
    [
        "mount/canary-0123abcd.session.json",
        "mount/acct-bank.session.json",
        "mount/MOUNT/CANARY-FF.SESSION.JSON",
        "browser-profiles/acct/Cookies",
        "soul/Browser-Profile/Default/Cookies",
        "soul/chromium-profile/state",
        "soul/cookie-profiles/current.bin",
    ];

    private static readonly string[] _ordinaryPaths =
    [
        "notes/decision.md",
        "mount/metadata.json",
        "soul/session.txt",
        "soul/profile-notes.md",
    ];

    [Theory]
    [MemberData(nameof(CookieLike))]
    public void Cookie_material_is_detected_by_default(string path)
    {
        Assert.True(BackupFileFilter.IsCookieProfilePath(path));
        Assert.False(new BackupFileFilter().ShouldInclude(path));
        Assert.False(new BackupFileFilter(includeCookieProfiles: false).ShouldInclude(path));
    }

    [Theory]
    [MemberData(nameof(CookieLike))]
    public void Cookie_material_is_included_only_when_the_flag_is_true(string path)
    {
        Assert.True(new BackupFileFilter(includeCookieProfiles: true).ShouldInclude(path));
    }

    [Theory]
    [MemberData(nameof(Ordinary))]
    public void Ordinary_files_are_included_for_both_flag_values(string path)
    {
        Assert.False(BackupFileFilter.IsCookieProfilePath(path));
        Assert.True(new BackupFileFilter(includeCookieProfiles: false).ShouldInclude(path));
        Assert.True(new BackupFileFilter(includeCookieProfiles: true).ShouldInclude(path));
    }

    [Fact]
    public void Default_flag_is_false()
    {
        Assert.False(new BackupFileFilter().IncludeCookieProfiles);
    }

    [Fact]
    public void Selection_leaves_cookie_files_out_by_default_and_keeps_everything_when_included()
    {
        using var dir = new TempDir();
        WriteFiles(dir.Path, [.. _ordinaryPaths, .. _cookieLikePaths]);

        var excluded = new BackupFileFilter(includeCookieProfiles: false).SelectIncludedFiles(dir.Path);
        var included = new BackupFileFilter(includeCookieProfiles: true).SelectIncludedFiles(dir.Path);

        Assert.Equal(_ordinaryPaths.Order(StringComparer.Ordinal), excluded);
        Assert.Equal((_ordinaryPaths.Concat(_cookieLikePaths)).Order(StringComparer.Ordinal), included);
    }

    [Fact]
    public void Selection_of_a_missing_directory_is_an_error()
    {
        using var dir = new TempDir();

        Assert.Throws<DirectoryNotFoundException>(() => new BackupFileFilter().SelectIncludedFiles(dir.Path));
    }

    [Fact]
    public void Restic_patterns_are_empty_when_cookie_profiles_are_included()
    {
        Assert.Empty(new BackupFileFilter(includeCookieProfiles: true).ResticExcludePatterns);
        Assert.Contains("*.session.json", new BackupFileFilter().ResticExcludePatterns);
        Assert.Contains("browser-profiles", new BackupFileFilter().ResticExcludePatterns);
    }

    [Fact]
    public async Task Restic_upload_excludes_cookie_profiles_by_default()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, "{\"snapshot_id\":\"s1\"}\n", string.Empty));
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var target = new ResticBackupTarget(runner, "restic", "r", new Dictionary<string, string>());

        await target.PutDirectoryAsync(dir.Path, ["soul"], TestContext.Current.CancellationToken);

        var args = Assert.Single(runner.Calls).Args;
        Assert.Contains("--exclude=*.session.json", args);
        Assert.Contains("--exclude=browser-profiles", args);
        Assert.Contains("--exclude=cookie-profiles", args);
        Assert.Contains(dir.Path, args);
        Assert.Contains("--tag=soul", args);
        // The source directory stays a positional argument after the excludes, and the tags keep their order.
        Assert.True(args.ToList().IndexOf(dir.Path) > args.ToList().IndexOf("--exclude=*.session.json"));
    }

    [Fact]
    public async Task Restic_upload_passes_no_exclusions_when_cookie_profiles_are_included()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, "{\"snapshot_id\":\"s2\"}\n", string.Empty));
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var target = new ResticBackupTarget(runner, "restic", "r", new Dictionary<string, string>(), includeCookieProfiles: true);

        var snap = await target.PutDirectoryAsync(dir.Path, [], TestContext.Current.CancellationToken);

        Assert.Equal("s2", snap.SnapshotId);
        Assert.DoesNotContain(Assert.Single(runner.Calls).Args, a => a.StartsWith("--exclude", StringComparison.Ordinal));
        Assert.True(target.FileFilter.IncludeCookieProfiles);
    }

    public static TheoryData<string> CookieLike() => ToData(_cookieLikePaths);

    public static TheoryData<string> Ordinary() => ToData(_ordinaryPaths);

    private static TheoryData<string> ToData(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (var value in values)
            data.Add(value);
        return data;
    }

    private static void WriteFiles(string root, IEnumerable<string> relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }
}
