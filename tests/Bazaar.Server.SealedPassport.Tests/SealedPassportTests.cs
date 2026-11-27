using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.SealedGit;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedPassport.Tests;

public sealed class SealedPassportTests : IDisposable
{
    private const string _canary = "CANARY-session-cookie-do-not-leak-4412";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-passport-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _audit = new();
    private readonly InMemorySealAnchor _anchor = new();

    public SealedPassportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SealedRoot => Path.Combine(_dir, "sealed");

    private string MountRoot => Path.Combine(_dir, "mnt");

    private SealedStore Store(long? maxBulkBytes = null) =>
        new(
            maxBulkBytes is { } max ? new SealedStoreOptions { RootPath = SealedRoot, MaxBulkBytes = max } : new SealedStoreOptions { RootPath = SealedRoot },
            FileKekProvider.Open(Path.Combine(_dir, "kek.key")),
            _anchor,
            _audit);

    private (SealedPassportStore Passport, SealedProfileDirectories Folders, SealedPassportImporter Importer) Build(IMountRootVerifier? verifier = null, string? mountRoot = "", long? maxBulkBytes = null)
    {
        var store = Store(maxBulkBytes);
        var passport = new SealedPassportStore(store, _audit);
        var options = new SealedPassportOptions { AllowPlaintextProfileFolders = true, MountRoot = mountRoot == "" ? MountRoot : mountRoot, LeaseTimeout = TimeSpan.FromSeconds(1) };
        return (passport, new SealedProfileDirectories(store, passport, options, verifier ?? new FixedVerifier(true)), new SealedPassportImporter(store, passport, options));
    }

    private string ScopeDir(string scope) =>
        Path.Combine(SealedRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32]);

    private static BrowserCookieEntry Cookie(string domain = "github.com", string value = _canary, DateTimeOffset? expires = null) =>
        new("session", value, domain, "/", expires);

    private void AssertNoCanaryOnDisk()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("kek.key", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(_canary, Encoding.Latin1.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cookies_round_trip_through_the_sealed_store()
    {
        var (passport, _, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["GitHub.com"], default);

        var found = await passport.FindProfileAsync(profile.ProfileId, default);
        Assert.Equal((profile.AgentId, profile.Class), (found!.AgentId, found.Class));
        Assert.Equal(["github.com"], found.ServiceAllowlist);
        Assert.Equal([profile.ProfileId], (await passport.ListProfilesAsync("agent-1", default)).Select(p => p.ProfileId));
        Assert.Equal(0, (await passport.ReadCookiesAsync("agent-1", profile.ProfileId, default)).Generation);

        var generation = await passport.WriteCookiesAsync("agent-1", profile.ProfileId, [Cookie("api.github.com")], 0, default);
        var jar = await passport.ReadCookiesAsync("agent-1", profile.ProfileId, default);
        Assert.Equal(generation, jar.Generation);
        Assert.Equal(_canary, Assert.Single(jar.Cookies).Value);

        // A second store over the same folders reads the same data.
        Assert.Equal(_canary, Assert.Single((await new SealedPassportStore(Store(), _audit).ReadCookiesAsync("agent-1", profile.ProfileId, default)).Cookies).Value);
    }

    [Fact]
    public async Task No_cookie_value_or_profile_file_is_plaintext_on_disk()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await passport.WriteCookiesAsync("agent-1", profile.ProfileId, [Cookie()], 0, default);
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "Cookies"), _canary);
            await folder.ResealAsync(default);
        }

        AssertNoCanaryOnDisk();
        Assert.Empty(Directory.EnumerateFileSystemEntries(MountRoot));
    }

    [Fact]
    public async Task A_tampered_jar_fails_closed_with_a_clear_error_and_an_audit_entry()
    {
        var (passport, _, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await passport.WriteCookiesAsync("agent-1", profile.ProfileId, [Cookie()], 0, default);
        var file = Directory.GetFiles(ScopeDir("cookies:" + profile.ProfileId.ToString("D")), "*.bzs").Single();
        var bytes = File.ReadAllBytes(file);
        bytes[^20] ^= 0x01;
        File.WriteAllBytes(file, bytes);

        var ex = await Assert.ThrowsAsync<PassportProfileUnavailableException>(() => passport.ReadCookiesAsync("agent-1", profile.ProfileId, default));
        Assert.Equal(profile.ProfileId, ex.ProfileId);
        Assert.IsType<SealTamperedException>(ex.InnerException);
        Assert.DoesNotContain(_canary, ex.ToString(), StringComparison.Ordinal);
        Assert.Contains(_audit.Events, e => e.Kind == "seal.tamper");
        Assert.Contains(_audit.Events, e => e.Kind == "passport.tamper" && e.Scope == "cookies:" + profile.ProfileId.ToString("D"));
        Assert.DoesNotContain(_audit.Events, e => (e.Detail + e.Path).Contains(_canary, StringComparison.Ordinal));

        // The browser port fails closed too.
        var source = new SealedBrowserCookieSource(passport);
        await Assert.ThrowsAsync<PassportProfileUnavailableException>(() => source.GetCookiesAsync("s1", profile, default));
    }

    [Fact]
    public async Task Tampered_profile_metadata_fails_closed()
    {
        var (passport, _, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var file = Directory.GetFiles(ScopeDir("passport:agent-1"), "*.bzs").Single();
        File.WriteAllBytes(file, File.ReadAllBytes(file)[..12]);

        await Assert.ThrowsAsync<PassportProfileUnavailableException>(() => passport.FindProfileAsync(profile.ProfileId, default));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.tamper");
    }

    [Fact]
    public async Task Deleting_a_profile_crypto_shreds_its_scopes()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await passport.WriteCookiesAsync("agent-1", profile.ProfileId, [Cookie()], 0, default);
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "Login Data"), "secret");
        }

        var cookieDir = ScopeDir("cookies:" + profile.ProfileId.ToString("D"));
        var folderDir = ScopeDir("browserdir:" + profile.ProfileId.ToString("D"));
        Assert.True(File.Exists(Path.Combine(cookieDir, "scope.key")));

        Assert.True(await passport.DeleteProfileAsync("agent-1", profile.ProfileId, default));

        Assert.False(Directory.Exists(cookieDir));
        Assert.False(Directory.Exists(folderDir));
        Assert.Null(await passport.FindProfileAsync(profile.ProfileId, default));
        Assert.Empty(await passport.ListProfilesAsync("agent-1", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => passport.ReadCookiesAsync("agent-1", profile.ProfileId, default));
        Assert.Contains(_audit.Events, e => e.Kind == "seal.shred" && e.Scope == "cookies:" + profile.ProfileId.ToString("D"));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profile.shredded");
        Assert.False(await passport.DeleteProfileAsync("agent-1", profile.ProfileId, default));
    }

    [Fact]
    public async Task Shredding_an_agent_removes_every_profile_and_its_scope()
    {
        var (passport, _, _) = Build();
        var a = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var b = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Anonymous, [], default);
        var other = await passport.CreateProfileAsync("agent-2", BrowserProfileClass.Anonymous, [], default);
        await passport.WriteCookiesAsync("agent-1", a.ProfileId, [Cookie()], 0, default);

        await passport.ShredAgentAsync("agent-1", default);

        Assert.Null(await passport.FindProfileAsync(a.ProfileId, default));
        Assert.Null(await passport.FindProfileAsync(b.ProfileId, default));
        Assert.NotNull(await passport.FindProfileAsync(other.ProfileId, default));
        Assert.False(Directory.Exists(ScopeDir("passport:agent-1")));
        AssertNoCanaryOnDisk();
    }

    [Fact]
    public async Task A_stale_concurrent_write_is_rejected()
    {
        var (passport, _, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var first = await passport.WriteCookiesAsync("agent-1", profile.ProfileId, [Cookie(value: "one")], 0, default);
        await Assert.ThrowsAsync<SealGenerationConflictException>(() => passport.WriteCookiesAsync("agent-1", profile.ProfileId, [Cookie(value: "two")], 0, default));

        // Two writers racing from the same generation: exactly one wins, the other gets a conflict and changes nothing.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
            {
                try
                {
                    await new SealedPassportStore(Store(), _audit).WriteCookiesAsync("agent-1", profile.ProfileId, [Cookie(value: "race-" + i)], first, default);
                    return true;
                }
                catch (SealGenerationConflictException)
                {
                    return false;
                }
            })));
        Assert.Single(results, r => r);
        Assert.StartsWith("race-", Assert.Single((await passport.ReadCookiesAsync("agent-1", profile.ProfileId, default)).Cookies).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_profiles_take_no_cookies_and_account_cookies_stay_in_the_allowlist()
    {
        var (passport, _, _) = Build();
        var anonymous = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Anonymous, [], default);
        var account = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => passport.WriteCookiesAsync("agent-1", anonymous.ProfileId, [Cookie()], 0, default));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => passport.WriteCookiesAsync("agent-1", account.ProfileId, [Cookie("evilgithub.com")], 0, default));
        Assert.DoesNotContain(_canary, ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => passport.CreateProfileAsync("../x", BrowserProfileClass.Anonymous, [], default));

        var source = new SealedBrowserCookieSource(passport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetCookiesAsync("s", anonymous, default));
        await passport.WriteCookiesAsync("agent-1", account.ProfileId, [Cookie(), Cookie(value: "old", expires: DateTimeOffset.UtcNow.AddDays(-1))], 0, default);
        Assert.Equal(_canary, Assert.Single(await source.GetCookiesAsync("s", account, default)).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetCookiesAsync("s", account with { AgentId = "agent-2" }, default));
    }

    [Fact]
    public async Task A_profile_folder_is_restored_resealed_and_wiped_on_close()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        string path;
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            path = folder.Path;
            Assert.True(SealedPaths.IsStrictlyInside(path, SealedPaths.Canonicalize(MountRoot)));
            Directory.CreateDirectory(Path.Combine(path, "Default", "Cache"));
            await File.WriteAllTextAsync(Path.Combine(path, "Default", "Preferences"), "prefs-v1");
            await File.WriteAllTextAsync(Path.Combine(path, "Default", "Cache", "data_0"), "cache");
            await File.WriteAllTextAsync(Path.Combine(path, "Local State"), "gone-next-time");
            await Assert.ThrowsAsync<InvalidOperationException>(() => folders.OpenAsync("agent-1", profile.ProfileId, default));
        }

        Assert.False(Directory.Exists(path));
        Assert.False(File.Exists(path + SealedProfileDirectories.MarkerSuffix));

        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            Assert.Equal("prefs-v1", await File.ReadAllTextAsync(Path.Combine(folder.Path, "Default", "Preferences")));
            Assert.False(File.Exists(Path.Combine(folder.Path, "Default", "Cache", "data_0")));
            File.Delete(Path.Combine(folder.Path, "Local State"));
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "Default", "Preferences"), "prefs-v2");
        }

        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            Assert.Equal("prefs-v2", await File.ReadAllTextAsync(Path.Combine(folder.Path, "Default", "Preferences")));
            Assert.False(File.Exists(Path.Combine(folder.Path, "Local State")));
        }
    }

    [Fact]
    public async Task An_anonymous_profile_folder_is_never_sealed()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Anonymous, [], default);
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "History"), _canary);
        }

        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
        }

        AssertNoCanaryOnDisk();
    }

    [Fact]
    public async Task Profile_folders_are_refused_without_a_confirmed_mount_root()
    {
        var store = Store();
        var passport = new SealedPassportStore(store, _audit);
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);

        var noRoot = new SealedProfileDirectories(store, passport, new SealedPassportOptions { AllowPlaintextProfileFolders = true }, new FixedVerifier(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => noRoot.OpenAsync("agent-1", profile.ProfileId, default));

        var noVerifier = new SealedProfileDirectories(store, passport, new SealedPassportOptions { AllowPlaintextProfileFolders = true, MountRoot = MountRoot }, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => noVerifier.OpenAsync("agent-1", profile.ProfileId, default));

        var plainDisk = new SealedProfileDirectories(store, passport, new SealedPassportOptions { AllowPlaintextProfileFolders = true, MountRoot = MountRoot }, new FixedVerifier(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plainDisk.OpenAsync("agent-1", profile.ProfileId, default));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.mount_unverified");
        Assert.False(Directory.Exists(MountRoot) && Directory.EnumerateFileSystemEntries(MountRoot).Any());

        if (!OperatingSystem.IsWindows())
        {
            var real = Path.Combine(_dir, "real-root");
            Directory.CreateDirectory(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var link = Path.Combine(_dir, "link-root");
            Directory.CreateSymbolicLink(link, real);
            var linked = new SealedProfileDirectories(store, passport, new SealedPassportOptions { AllowPlaintextProfileFolders = true, MountRoot = link }, new FixedVerifier(true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => linked.OpenAsync("agent-1", profile.ProfileId, default));

            var open = Path.Combine(_dir, "open-root");
            Directory.CreateDirectory(open);
            File.SetUnixFileMode(open, (UnixFileMode)0b111_101_101);
            var groupReadable = new SealedProfileDirectories(store, passport, new SealedPassportOptions { AllowPlaintextProfileFolders = true, MountRoot = open }, new FixedVerifier(true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => groupReadable.OpenAsync("agent-1", profile.ProfileId, default));
        }
    }

    [Fact]
    public async Task A_crashed_folder_is_wiped_and_an_unknown_folder_is_refused_untouched()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var folderPath = Path.Combine(MountRoot, "profile-" + profile.ProfileId.ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(MountRoot);
        }
        else
        {
            Directory.CreateDirectory(MountRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.CreateDirectory(folderPath);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "Cookies"), "not ours");

        await Assert.ThrowsAsync<InvalidOperationException>(() => folders.OpenAsync("agent-1", profile.ProfileId, default));
        Assert.Equal("not ours", await File.ReadAllTextAsync(Path.Combine(folderPath, "Cookies")));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.refused");

        await File.WriteAllBytesAsync(folderPath + SealedProfileDirectories.MarkerSuffix, []);
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
        }

        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.stale_wiped");
    }

    [Fact]
    public async Task Deleting_a_profile_is_refused_while_its_folder_is_open()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await using (await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => passport.DeleteProfileAsync("agent-1", profile.ProfileId, default));
        }

        Assert.NotNull(await passport.FindProfileAsync(profile.ProfileId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => passport.DeleteProfileAsync("agent-2", profile.ProfileId, default));
    }

    [Fact]
    public async Task A_tampered_profile_folder_fails_closed_and_leaves_nothing_on_disk()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "Cookies"), _canary);
        }

        var file = Directory.GetFiles(ScopeDir("browserdir:" + profile.ProfileId.ToString("D")), "*.bzs").Single();
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(file, bytes);

        await Assert.ThrowsAsync<PassportProfileUnavailableException>(() => folders.OpenAsync("agent-1", profile.ProfileId, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(MountRoot));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.tamper");
    }

    [Fact]
    public async Task Import_seals_verifies_then_wipes_the_plaintext_folder()
    {
        var (passport, folders, importer) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var source = Path.Combine(_dir, "legacy-profile");
        Directory.CreateDirectory(Path.Combine(source, "Default"));
        await File.WriteAllTextAsync(Path.Combine(source, "Default", "Cookies"), _canary);
        await File.WriteAllTextAsync(Path.Combine(source, "Local State"), "{}");

        var report = await importer.ImportProfileDirectoryAsync("agent-1", profile.ProfileId, source, default);

        Assert.Equal(2, report.Imported);
        Assert.Equal(2, report.RemovedPlaintext);
        Assert.True(report.SourceRemoved);
        Assert.False(Directory.Exists(source));
        AssertNoCanaryOnDisk();
        Assert.Contains(_audit.Events, e => e.Kind == "passport.import");

        await using var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default);
        Assert.Equal(_canary, await File.ReadAllTextAsync(Path.Combine(folder.Path, "Default", "Cookies")));
    }

    [Fact]
    public async Task Import_refuses_anonymous_profiles_and_keeps_their_plaintext()
    {
        var (passport, _, importer) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Anonymous, [], default);
        var source = Path.Combine(_dir, "legacy-anon");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "History"), "h");

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportProfileDirectoryAsync("agent-1", profile.ProfileId, source, default));
        Assert.True(File.Exists(Path.Combine(source, "History")));
    }

    private static void MakeOwnerOnlyRoot(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string FolderOf(string mountRoot, Guid profileId) => Path.Combine(mountRoot, "profile-" + profileId.ToString("N"));

    private static void HardLink(string existing, string link)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln", [existing, link]) { RedirectStandardError = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Plaintext_profile_folders_are_refused_and_audited_unless_the_operator_allows_them()
    {
        var store = Store();
        var passport = new SealedPassportStore(store, _audit);
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);

        // A mount root and a protective verifier are not enough on their own: the default setting is false.
        var denied = new SealedProfileDirectories(store, passport, new SealedPassportOptions { MountRoot = MountRoot }, new FixedVerifier(true));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => denied.OpenAsync("agent-1", profile.ProfileId, default));

        Assert.Contains("AllowPlaintextProfileFolders", ex.Message, StringComparison.Ordinal);
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.mount_unverified" && e.Detail.Contains("AllowPlaintextProfileFolders", StringComparison.Ordinal));
        Assert.False(Directory.Exists(MountRoot) && Directory.EnumerateFileSystemEntries(MountRoot).Any());
        Assert.False(new SealedPassportOptions().AllowPlaintextProfileFolders);
    }

    [Fact]
    public async Task A_marker_with_a_symlinked_folder_is_refused_and_the_target_is_untouched()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        MakeOwnerOnlyRoot(MountRoot);
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "victim"), "keep me");
        Directory.CreateSymbolicLink(FolderOf(MountRoot, profile.ProfileId), outside);
        await File.WriteAllBytesAsync(FolderOf(MountRoot, profile.ProfileId) + SealedProfileDirectories.MarkerSuffix, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => folders.OpenAsync("agent-1", profile.ProfileId, default));

        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(outside, "victim")));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.link_refused");
        Assert.True(new DirectoryInfo(FolderOf(MountRoot, profile.ProfileId)).LinkTarget is not null);
    }

    [Fact]
    public async Task A_marker_that_is_a_link_or_a_folder_is_refused_and_nothing_is_wiped()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        MakeOwnerOnlyRoot(MountRoot);
        var folder = FolderOf(MountRoot, profile.ProfileId);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "keep"), "keep me");
        var real = Path.Combine(_dir, "real-marker");
        await File.WriteAllBytesAsync(real, []);
        File.CreateSymbolicLink(folder + SealedProfileDirectories.MarkerSuffix, real);

        await Assert.ThrowsAsync<InvalidOperationException>(() => folders.OpenAsync("agent-1", profile.ProfileId, default));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(folder, "keep")));

        File.Delete(folder + SealedProfileDirectories.MarkerSuffix);
        Directory.CreateDirectory(folder + SealedProfileDirectories.MarkerSuffix);
        await Assert.ThrowsAsync<InvalidOperationException>(() => folders.OpenAsync("agent-1", profile.ProfileId, default));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(folder, "keep")));
        Assert.Equal(2, _audit.Events.Count(e => e.Kind == "passport.profiledir.link_refused"));
    }

    [Fact]
    public async Task A_link_swapped_in_after_the_walk_is_deleted_not_followed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "victim"), "keep me");
        var root = Path.Combine(_dir, "wipe-me");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(root, "file"), "secret");
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "inner"), "secret");

        var failure = SecureFileWipe.WipeTree(root, () =>
        {
            File.Delete(Path.Combine(root, "file"));
            File.CreateSymbolicLink(Path.Combine(root, "file"), Path.Combine(outside, "victim"));
            File.Delete(Path.Combine(root, "sub", "inner"));
            Directory.Delete(Path.Combine(root, "sub"));
            Directory.CreateSymbolicLink(Path.Combine(root, "sub"), outside);
        });

        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(outside, "victim")));
        Assert.NotNull(failure);
        Assert.Equal(["victim"], Directory.GetFiles(outside).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Links_inside_the_folder_are_deleted_at_close_and_their_targets_survive()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "victim"), "keep me");
        string path;
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            path = folder.Path;
            File.CreateSymbolicLink(Path.Combine(path, "SingletonLock"), Path.Combine(outside, "victim"));
            Directory.CreateSymbolicLink(Path.Combine(path, "dirlink"), outside);
            await File.WriteAllTextAsync(Path.Combine(path, "Cookies"), "c");
        }

        Assert.False(Directory.Exists(path));
        Assert.False(File.Exists(path + SealedProfileDirectories.MarkerSuffix));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(outside, "victim")));
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_before_close_is_not_wiped_through_the_link_and_keeps_its_marker()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "victim"), "keep me");
        string path;
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            path = folder.Path;
            Directory.Delete(path, recursive: true);
            Directory.CreateSymbolicLink(path, outside);
        }

        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(outside, "victim")));
        Assert.True(File.Exists(path + SealedProfileDirectories.MarkerSuffix));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.wipe_failed");
    }

    [Fact]
    public async Task A_hard_linked_file_is_deleted_without_zeroing_the_other_name()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var outside = Path.Combine(_dir, "outside-file");
        await File.WriteAllTextAsync(outside, "keep me");
        var root = Path.Combine(_dir, "wipe-hard");
        Directory.CreateDirectory(root);
        HardLink(outside, Path.Combine(root, "shared"));

        Assert.Equal(WipeOutcome.DeletedShared, SecureFileWipe.ZeroAndDelete(Path.Combine(root, "shared"), root));

        Assert.Equal("keep me", await File.ReadAllTextAsync(outside));
        Assert.False(File.Exists(Path.Combine(root, "shared")));
    }

    [Fact]
    public void The_windows_root_acl_rule_allows_only_the_service_account_system_and_administrators()
    {
        HashSet<string> permitted = ["S-1-5-21-1", "S-1-5-18", "S-1-5-32-544"];

        Assert.True(RootAcl.IsOwnerOnly([("S-1-5-21-1", true), ("S-1-5-18", true), ("S-1-1-0", false)], permitted, out _));
        Assert.False(RootAcl.IsOwnerOnly([("S-1-5-21-1", true), ("S-1-1-0", true)], permitted, out var offender));
        Assert.Equal("S-1-1-0", offender);
    }

    [Fact]
    public async Task A_profile_deleted_between_the_lookup_and_the_lease_is_not_opened()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        folders.AfterProfileLookup = async () => Assert.True(await passport.DeleteProfileAsync("agent-1", profile.ProfileId, default));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => folders.OpenAsync("agent-1", profile.ProfileId, default));

        Assert.False(Directory.Exists(MountRoot) && Directory.EnumerateFileSystemEntries(MountRoot).Any());
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.refused" && e.Detail.Contains("deleted", StringComparison.Ordinal));
        Assert.Null(await passport.FindProfileAsync(profile.ProfileId, default));
    }

    [Fact]
    public async Task A_delete_that_holds_the_folder_lease_makes_a_concurrent_open_wait_and_then_refuse()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);

        var outcomes = await Task.WhenAll(
            Task.Run(async () =>
            {
                try
                {
                    await using var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default);
                    return "opened";
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                {
                    return "refused";
                }
            }),
            Task.Run(async () =>
            {
                try
                {
                    return await passport.DeleteProfileAsync("agent-1", profile.ProfileId, default) ? "deleted" : "missing";
                }
                catch (InvalidOperationException)
                {
                    return "busy";
                }
            }));

        // Whatever the interleaving, a deleted profile never leaves a folder or a sealed folder scope behind.
        if (outcomes[1] == "deleted")
        {
            Assert.Null(await passport.FindProfileAsync(profile.ProfileId, default));
            Assert.False(Directory.Exists(MountRoot) && Directory.EnumerateFileSystemEntries(MountRoot).Any());
        }

        Assert.True(outcomes[0] is "opened" or "refused");
    }

    [Fact]
    public async Task The_start_sweep_wipes_every_marked_folder_and_leaves_unknown_markers()
    {
        var (passport, folders, _) = Build();
        var live = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var gone = Guid.NewGuid();
        MakeOwnerOnlyRoot(MountRoot);
        foreach (var id in new[] { live.ProfileId, gone })
        {
            var folder = FolderOf(MountRoot, id);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "Cookies"), _canary);
            await File.WriteAllBytesAsync(folder + SealedProfileDirectories.MarkerSuffix, []);
        }

        await File.WriteAllBytesAsync(Path.Combine(MountRoot, "stranger" + SealedProfileDirectories.MarkerSuffix), []);
        Directory.CreateDirectory(Path.Combine(MountRoot, "unmarked"));

        Assert.Equal(2, await folders.SweepStaleAsync(default));

        Assert.False(Directory.Exists(FolderOf(MountRoot, live.ProfileId)));
        Assert.False(Directory.Exists(FolderOf(MountRoot, gone)));
        Assert.True(File.Exists(Path.Combine(MountRoot, "stranger" + SealedProfileDirectories.MarkerSuffix)));
        Assert.True(Directory.Exists(Path.Combine(MountRoot, "unmarked")));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.unknown_marker");
        AssertNoCanaryOnDisk();
    }

    [Fact]
    public async Task The_start_sweep_leaves_a_folder_whose_lease_another_holder_has()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await using var open = await folders.OpenAsync("agent-1", profile.ProfileId, default);
        var other = new SealedProfileDirectories(Store(), passport, new SealedPassportOptions { AllowPlaintextProfileFolders = true, MountRoot = MountRoot }, new FixedVerifier(true));

        Assert.Equal(0, await other.SweepStaleAsync(default));

        Assert.True(Directory.Exists(open.Path));
        Assert.True(File.Exists(open.Path + SealedProfileDirectories.MarkerSuffix));
    }

    [Fact]
    public async Task The_hosted_sweeper_runs_the_sweep_at_start()
    {
        var (passport, folders, _) = Build();
        var gone = Guid.NewGuid();
        MakeOwnerOnlyRoot(MountRoot);
        Directory.CreateDirectory(FolderOf(MountRoot, gone));
        await File.WriteAllBytesAsync(FolderOf(MountRoot, gone) + SealedProfileDirectories.MarkerSuffix, []);

        await new ProfileFolderSweeper(folders).StartAsync(default);

        Assert.False(Directory.Exists(FolderOf(MountRoot, gone)));
        _ = passport;
    }

    [Fact]
    public async Task Deleting_a_profile_wipes_its_leftover_marked_folder()
    {
        var (passport, _, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        MakeOwnerOnlyRoot(MountRoot);
        var folder = FolderOf(MountRoot, profile.ProfileId);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Cookies"), _canary);
        await File.WriteAllBytesAsync(folder + SealedProfileDirectories.MarkerSuffix, []);

        Assert.True(await passport.DeleteProfileAsync("agent-1", profile.ProfileId, default));

        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(folder + SealedProfileDirectories.MarkerSuffix));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.purged");
    }

    [Fact]
    public async Task Erasure_carries_on_through_tampered_metadata_and_a_tampered_index_entry()
    {
        var (passport, _, _) = Build();
        var a = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var b = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["gitlab.com"], default);
        var other = await passport.CreateProfileAsync("agent-2", BrowserProfileClass.Anonymous, [], default);
        await passport.WriteCookiesAsync("agent-1", a.ProfileId, [Cookie()], 0, default);

        // Truncate every sealed file of the agent scope (the metadata) and flip a bit in one index entry file.
        foreach (var file in Directory.GetFiles(ScopeDir("passport:agent-1"), "*.bzs"))
        {
            File.WriteAllBytes(file, File.ReadAllBytes(file)[..12]);
        }

        var indexFile = Directory.GetFiles(ScopeDir("passport-index"), "*.bzs").First();
        var bytes = File.ReadAllBytes(indexFile);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(indexFile, bytes);

        await passport.ShredAgentAsync("agent-1", default);

        Assert.False(Directory.Exists(ScopeDir("passport:agent-1")));
        Assert.False(Directory.Exists(ScopeDir("cookies:" + a.ProfileId.ToString("D"))));
        Assert.False(Directory.Exists(ScopeDir("browserdir:" + a.ProfileId.ToString("D"))));
        Assert.False(Directory.Exists(ScopeDir("cookies:" + b.ProfileId.ToString("D"))));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.tamper");
        Assert.Contains(_audit.Events, e => e.Kind == "passport.agent.shredded");
        Assert.Equal([other.ProfileId], (await passport.ListProfilesAsync("agent-2", default)).Select(p => p.ProfileId));
        AssertNoCanaryOnDisk();
    }

    [Fact]
    public async Task Erasure_ignores_a_listing_that_cannot_be_verified()
    {
        var (passport, _, _) = Build();
        var a = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await passport.WriteCookiesAsync("agent-1", a.ProfileId, [Cookie()], 0, default);
        var manifest = Directory.GetFiles(ScopeDir("passport:agent-1")).Where(f => !f.EndsWith(".bzs", StringComparison.Ordinal) && !f.EndsWith("scope.key", StringComparison.Ordinal) && !f.EndsWith(".lock", StringComparison.Ordinal) && !f.EndsWith(".lease", StringComparison.Ordinal)).ToList();
        foreach (var file in manifest)
        {
            File.WriteAllBytes(file, [1, 2, 3]);
        }

        await passport.ShredAgentAsync("agent-1", default);

        Assert.False(Directory.Exists(ScopeDir("passport:agent-1")));
        AssertNoCanaryOnDisk();
    }

    [Fact]
    public async Task A_reseal_that_would_pass_the_bulk_limit_is_refused_and_the_sealed_copy_stays()
    {
        var (passport, folders, _) = Build(maxBulkBytes: 4096);
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "Preferences"), "small");
            await folder.ResealAsync(default);
            await File.WriteAllBytesAsync(Path.Combine(folder.Path, "Big"), new byte[5000]);

            await Assert.ThrowsAsync<InvalidOperationException>(() => folder.ResealAsync(default));
        }

        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.reseal_refused");
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.reseal_failed");
        await using var again = await folders.OpenAsync("agent-1", profile.ProfileId, default);
        Assert.Equal("small", await File.ReadAllTextAsync(Path.Combine(again.Path, "Preferences")));
        Assert.False(File.Exists(Path.Combine(again.Path, "Big")));
    }

    [Fact]
    public async Task Reseal_uses_the_stores_path_rules_and_skips_names_it_cannot_seal()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var decomposed = "Café";
        await using (var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default))
        {
            await File.WriteAllTextAsync(Path.Combine(folder.Path, decomposed), "nfc");
            if (!OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(Path.Combine(folder.Path, "has:colon"), "unsupported");
            }

            await File.WriteAllTextAsync(Path.Combine(folder.Path, "Preferences"), "ok");
            await folder.ResealAsync(default);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.name_skipped");
        }

        await using var again = await folders.OpenAsync("agent-1", profile.ProfileId, default);
        Assert.Equal("ok", await File.ReadAllTextAsync(Path.Combine(again.Path, "Preferences")));
        Assert.Equal("nfc", await File.ReadAllTextAsync(Path.Combine(again.Path, decomposed.Normalize(System.Text.NormalizationForm.FormC))));
        Assert.False(File.Exists(Path.Combine(again.Path, "has:colon")));
    }

    [Fact]
    public async Task Import_skips_the_excluded_cache_folders_and_leaves_them_in_place()
    {
        var (passport, folders, importer) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var source = Path.Combine(_dir, "legacy-profile");
        Directory.CreateDirectory(Path.Combine(source, "Default", "Cache"));
        await File.WriteAllTextAsync(Path.Combine(source, "Default", "Cache", "data_0"), "cache");
        await File.WriteAllTextAsync(Path.Combine(source, "Default", "Preferences"), "prefs");

        var report = await importer.ImportProfileDirectoryAsync("agent-1", profile.ProfileId, source, default);

        Assert.Equal(1, report.Imported);
        Assert.False(report.SourceRemoved);
        Assert.True(File.Exists(Path.Combine(source, "Default", "Cache", "data_0")));
        Assert.False(File.Exists(Path.Combine(source, "Default", "Preferences")));
        await using var folder = await folders.OpenAsync("agent-1", profile.ProfileId, default);
        Assert.False(File.Exists(Path.Combine(folder.Path, "Default", "Cache", "data_0")));
        Assert.Equal("prefs", await File.ReadAllTextAsync(Path.Combine(folder.Path, "Default", "Preferences")));
    }

    [Fact]
    public async Task Import_never_zeroes_a_hard_link_target_and_refuses_a_linked_source()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var (passport, _, importer) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        var outside = Path.Combine(_dir, "outside-file");
        await File.WriteAllTextAsync(outside, "keep me");
        var source = Path.Combine(_dir, "legacy-hard");
        Directory.CreateDirectory(source);
        HardLink(outside, Path.Combine(source, "shared"));

        await importer.ImportProfileDirectoryAsync("agent-1", profile.ProfileId, source, default);

        Assert.Equal("keep me", await File.ReadAllTextAsync(outside));

        var link = Path.Combine(_dir, "source-link");
        Directory.CreateSymbolicLink(link, _dir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportProfileDirectoryAsync("agent-1", profile.ProfileId, link, default));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.import.refused");
    }

    [Fact]
    public async Task Another_agents_profile_is_refused_by_every_call_and_stays_intact()
    {
        var (passport, folders, importer) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await passport.WriteCookiesAsync("agent-1", profile.ProfileId, [Cookie()], 0, default);
        var source = Path.Combine(_dir, "legacy-x");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "f"), "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => passport.ReadCookiesAsync("agent-2", profile.ProfileId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => passport.WriteCookiesAsync("agent-2", profile.ProfileId, [Cookie()], 1, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => folders.OpenAsync("agent-2", profile.ProfileId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportProfileDirectoryAsync("agent-2", profile.ProfileId, source, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => passport.DeleteProfileAsync("agent-2", profile.ProfileId, default));

        Assert.True(File.Exists(Path.Combine(source, "f")));
        Assert.NotNull(await passport.FindProfileAsync(profile.ProfileId, default));
        Assert.Equal(_canary, Assert.Single((await passport.ReadCookiesAsync("agent-1", profile.ProfileId, default)).Cookies).Value);
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profile.refused");
    }

    [Theory]
    [InlineData("com")]
    [InlineData("localhost")]
    [InlineData("co.uk")]
    [InlineData("GitHub.io")]
    [InlineData("github.io.")]
    public async Task Single_label_and_public_suffix_allowlist_entries_are_refused(string entry)
    {
        var (passport, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, [entry], default));
        Assert.Empty(await passport.ListProfilesAsync("agent-1", default));

        var ok = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["example.co.uk", "user.github.io"], default);
        Assert.Equal(["example.co.uk", "user.github.io"], ok.ServiceAllowlist);
    }

    private sealed class FixedVerifier(bool isProtected) : IMountRootVerifier
    {
        public MountRootVerdict Verify(string mountRoot) => new(isProtected, isProtected ? "test: protected" : "test: plain disk");
    }

    private sealed class RecordingSink : ISealAuditSink
    {
        private readonly ConcurrentQueue<SealAuditEvent> _events = new();

        public IReadOnlyCollection<SealAuditEvent> Events => _events;

        public ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            _events.Enqueue(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
