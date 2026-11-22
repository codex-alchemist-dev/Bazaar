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

    private SealedStore Store() =>
        new(new SealedStoreOptions { RootPath = SealedRoot }, FileKekProvider.Open(Path.Combine(_dir, "kek.key")), _anchor, _audit);

    private (SealedPassportStore Passport, SealedProfileDirectories Folders, SealedPassportImporter Importer) Build(IMountRootVerifier? verifier = null, string? mountRoot = "")
    {
        var store = Store();
        var passport = new SealedPassportStore(store, _audit);
        var options = new SealedPassportOptions { MountRoot = mountRoot == "" ? MountRoot : mountRoot, LeaseTimeout = TimeSpan.FromSeconds(1) };
        return (passport, new SealedProfileDirectories(store, passport, options, verifier ?? new FixedVerifier(true)), new SealedPassportImporter(store, passport));
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
        Assert.Equal(0, (await passport.ReadCookiesAsync(profile.ProfileId, default)).Generation);

        var generation = await passport.WriteCookiesAsync(profile.ProfileId, [Cookie("api.github.com")], 0, default);
        var jar = await passport.ReadCookiesAsync(profile.ProfileId, default);
        Assert.Equal(generation, jar.Generation);
        Assert.Equal(_canary, Assert.Single(jar.Cookies).Value);

        // A second store over the same folders reads the same data.
        Assert.Equal(_canary, Assert.Single((await new SealedPassportStore(Store(), _audit).ReadCookiesAsync(profile.ProfileId, default)).Cookies).Value);
    }

    [Fact]
    public async Task No_cookie_value_or_profile_file_is_plaintext_on_disk()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        await passport.WriteCookiesAsync(profile.ProfileId, [Cookie()], null, default);
        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
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
        await passport.WriteCookiesAsync(profile.ProfileId, [Cookie()], null, default);
        var file = Directory.GetFiles(ScopeDir("cookies:" + profile.ProfileId.ToString("D")), "*.bzs").Single();
        var bytes = File.ReadAllBytes(file);
        bytes[^20] ^= 0x01;
        File.WriteAllBytes(file, bytes);

        var ex = await Assert.ThrowsAsync<PassportProfileUnavailableException>(() => passport.ReadCookiesAsync(profile.ProfileId, default));
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
        await passport.WriteCookiesAsync(profile.ProfileId, [Cookie()], null, default);
        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
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
        await Assert.ThrowsAsync<KeyNotFoundException>(() => passport.ReadCookiesAsync(profile.ProfileId, default));
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
        await passport.WriteCookiesAsync(a.ProfileId, [Cookie()], null, default);

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
        var first = await passport.WriteCookiesAsync(profile.ProfileId, [Cookie(value: "one")], 0, default);
        await Assert.ThrowsAsync<SealGenerationConflictException>(() => passport.WriteCookiesAsync(profile.ProfileId, [Cookie(value: "two")], 0, default));

        // Two writers racing from the same generation: exactly one wins, the other gets a conflict and changes nothing.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
            {
                try
                {
                    await new SealedPassportStore(Store(), _audit).WriteCookiesAsync(profile.ProfileId, [Cookie(value: "race-" + i)], first, default);
                    return true;
                }
                catch (SealGenerationConflictException)
                {
                    return false;
                }
            })));
        Assert.Single(results, r => r);
        Assert.StartsWith("race-", Assert.Single((await passport.ReadCookiesAsync(profile.ProfileId, default)).Cookies).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_profiles_take_no_cookies_and_account_cookies_stay_in_the_allowlist()
    {
        var (passport, _, _) = Build();
        var anonymous = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Anonymous, [], default);
        var account = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => passport.WriteCookiesAsync(anonymous.ProfileId, [Cookie()], null, default));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => passport.WriteCookiesAsync(account.ProfileId, [Cookie("evilgithub.com")], null, default));
        Assert.DoesNotContain(_canary, ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => passport.CreateProfileAsync("../x", BrowserProfileClass.Anonymous, [], default));

        var source = new SealedBrowserCookieSource(passport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetCookiesAsync("s", anonymous, default));
        await passport.WriteCookiesAsync(account.ProfileId, [Cookie(), Cookie(value: "old", expires: DateTimeOffset.UtcNow.AddDays(-1))], null, default);
        Assert.Equal(_canary, Assert.Single(await source.GetCookiesAsync("s", account, default)).Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetCookiesAsync("s", account with { AgentId = "agent-2" }, default));
    }

    [Fact]
    public async Task A_profile_folder_is_restored_resealed_and_wiped_on_close()
    {
        var (passport, folders, _) = Build();
        var profile = await passport.CreateProfileAsync("agent-1", BrowserProfileClass.Account, ["github.com"], default);
        string path;
        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
        {
            path = folder.Path;
            Assert.True(SealedPaths.IsStrictlyInside(path, SealedPaths.Canonicalize(MountRoot)));
            Directory.CreateDirectory(Path.Combine(path, "Default", "Cache"));
            await File.WriteAllTextAsync(Path.Combine(path, "Default", "Preferences"), "prefs-v1");
            await File.WriteAllTextAsync(Path.Combine(path, "Default", "Cache", "data_0"), "cache");
            await File.WriteAllTextAsync(Path.Combine(path, "Local State"), "gone-next-time");
            await Assert.ThrowsAsync<InvalidOperationException>(() => folders.OpenAsync(profile.ProfileId, default));
        }

        Assert.False(Directory.Exists(path));
        Assert.False(File.Exists(path + SealedProfileDirectories.MarkerSuffix));

        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
        {
            Assert.Equal("prefs-v1", await File.ReadAllTextAsync(Path.Combine(folder.Path, "Default", "Preferences")));
            Assert.False(File.Exists(Path.Combine(folder.Path, "Default", "Cache", "data_0")));
            File.Delete(Path.Combine(folder.Path, "Local State"));
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "Default", "Preferences"), "prefs-v2");
        }

        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
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
        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
        {
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "History"), _canary);
        }

        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
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

        var noRoot = new SealedProfileDirectories(store, passport, new SealedPassportOptions(), new FixedVerifier(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => noRoot.OpenAsync(profile.ProfileId, default));

        var noVerifier = new SealedProfileDirectories(store, passport, new SealedPassportOptions { MountRoot = MountRoot }, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => noVerifier.OpenAsync(profile.ProfileId, default));

        var plainDisk = new SealedProfileDirectories(store, passport, new SealedPassportOptions { MountRoot = MountRoot }, new FixedVerifier(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plainDisk.OpenAsync(profile.ProfileId, default));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.mount_unverified");
        Assert.False(Directory.Exists(MountRoot) && Directory.EnumerateFileSystemEntries(MountRoot).Any());

        if (!OperatingSystem.IsWindows())
        {
            var real = Path.Combine(_dir, "real-root");
            Directory.CreateDirectory(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var link = Path.Combine(_dir, "link-root");
            Directory.CreateSymbolicLink(link, real);
            var linked = new SealedProfileDirectories(store, passport, new SealedPassportOptions { MountRoot = link }, new FixedVerifier(true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => linked.OpenAsync(profile.ProfileId, default));

            var open = Path.Combine(_dir, "open-root");
            Directory.CreateDirectory(open);
            File.SetUnixFileMode(open, (UnixFileMode)0b111_101_101);
            var groupReadable = new SealedProfileDirectories(store, passport, new SealedPassportOptions { MountRoot = open }, new FixedVerifier(true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => groupReadable.OpenAsync(profile.ProfileId, default));
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => folders.OpenAsync(profile.ProfileId, default));
        Assert.Equal("not ours", await File.ReadAllTextAsync(Path.Combine(folderPath, "Cookies")));
        Assert.Contains(_audit.Events, e => e.Kind == "passport.profiledir.refused");

        await File.WriteAllBytesAsync(folderPath + SealedProfileDirectories.MarkerSuffix, []);
        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
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
        await using (await folders.OpenAsync(profile.ProfileId, default))
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
        await using (var folder = await folders.OpenAsync(profile.ProfileId, default))
        {
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "Cookies"), _canary);
        }

        var file = Directory.GetFiles(ScopeDir("browserdir:" + profile.ProfileId.ToString("D")), "*.bzs").Single();
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(file, bytes);

        await Assert.ThrowsAsync<PassportProfileUnavailableException>(() => folders.OpenAsync(profile.ProfileId, default));
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

        var report = await importer.ImportProfileDirectoryAsync(profile.ProfileId, source, default);

        Assert.Equal(2, report.Imported);
        Assert.Equal(2, report.RemovedPlaintext);
        Assert.True(report.SourceRemoved);
        Assert.False(Directory.Exists(source));
        AssertNoCanaryOnDisk();
        Assert.Contains(_audit.Events, e => e.Kind == "passport.import");

        await using var folder = await folders.OpenAsync(profile.ProfileId, default);
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportProfileDirectoryAsync(profile.ProfileId, source, default));
        Assert.True(File.Exists(Path.Combine(source, "History")));
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
