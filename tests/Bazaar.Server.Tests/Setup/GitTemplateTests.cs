using System.Diagnostics;
using System.Text;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>Git template catalog: entry rules, the file parser, the catalog with a fake fetcher, the git CLI fetcher on a local repository, and the hash pin.</summary>
public sealed class GitTemplateTests
{
    private static readonly string[] _defaultSchemes = ["https", "ssh"];

    // Built from parts so no scanner sees a credential-shaped literal.
    private static readonly string _fakeToken = "tok" + "en-" + "abc123";

    private static readonly string[] _gitIdentity = ["-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false"];

    private static readonly string _commit = new('a', 40);
    private static readonly string _hash = new('b', 64);

    private static string Entry(string url = "https://example.test/t.git", string commit = "", string path = "templates/starter", string hash = "", string id = "starter", string version = "1.0.0") =>
        "{\"id\":\"" + id + "\",\"version\":\"" + version + "\",\"url\":\"" + url + "\",\"commit\":\"" + (commit.Length == 0 ? _commit : commit)
        + "\",\"path\":\"" + path + "\",\"templateHash\":\"" + (hash.Length == 0 ? _hash : hash) + "\"}";

    private static string Catalog(params string[] entries) =>
        "{\"schemaVersion\":1,\"templates\":[" + string.Join(',', entries) + "]}";

    private static GitTemplateCatalogParseResult Parse(string json, IReadOnlyList<string>? schemes = null) =>
        GitTemplateCatalogParser.Parse(Encoding.UTF8.GetBytes(json), schemes ?? _defaultSchemes);

    // ---- rules ----

    [Theory]
    [InlineData("https://example.test/org/repo.git")]
    [InlineData("ssh://git@example.test/org/repo.git")]
    public void Plain_https_and_ssh_urls_are_accepted(string url) =>
        Assert.Null(GitTemplateRules.CheckUrl(url, _defaultSchemes));

    [Theory]
    [InlineData("")]
    [InlineData("-upload-pack=x")]
    [InlineData("ext::sh -c x")]
    [InlineData("file:///tmp/repo")]
    [InlineData("http://example.test/repo.git")]
    [InlineData("https://example.test/repo.git?x=1")]
    [InlineData("https://example.test/repo.git#frag")]
    [InlineData("https://example.test/a b")]
    [InlineData("/local/path")]
    public void Unsafe_or_disallowed_urls_are_refused(string url) =>
        Assert.NotNull(GitTemplateRules.CheckUrl(url, _defaultSchemes));

    [Fact]
    public void A_url_with_credentials_is_refused_without_echoing_them()
    {
        var message = GitTemplateRules.CheckUrl("https://user:" + _fakeToken + "@example.test/r.git", _defaultSchemes);

        Assert.NotNull(message);
        Assert.DoesNotContain(_fakeToken, message, StringComparison.Ordinal);
        Assert.NotNull(GitTemplateRules.CheckUrl("https://" + _fakeToken + "@example.test/r.git", _defaultSchemes));
        Assert.NotNull(GitTemplateRules.CheckUrl("ssh://git:" + _fakeToken + "@example.test/r.git", _defaultSchemes));
    }

    [Fact]
    public void The_file_scheme_is_allowed_only_when_the_operator_lists_it() =>
        Assert.Null(GitTemplateRules.CheckUrl("file:///tmp/repo", ["file"]));

    [Theory]
    [InlineData("a" + "bc", false)]
    [InlineData("", false)]
    public void Only_full_lower_case_commit_ids_are_commits(string text, bool expected) =>
        Assert.Equal(expected, GitTemplateRules.IsCommitId(text));

    [Fact]
    public void Commit_ids_of_forty_and_sixty_four_hex_characters_are_accepted_and_names_are_not()
    {
        Assert.True(GitTemplateRules.IsCommitId(_commit));
        Assert.True(GitTemplateRules.IsCommitId(new string('1', 64)));
        Assert.False(GitTemplateRules.IsCommitId("main"));
        Assert.False(GitTemplateRules.IsCommitId(new string('A', 40)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("templates/starter")]
    public void Plain_subfolders_are_accepted(string path) => Assert.Null(GitTemplateRules.CheckSubfolder(path));

    [Theory]
    [InlineData("/abs")]
    [InlineData("../up")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData(".git/hooks")]
    [InlineData("a/.git")]
    [InlineData("a\\b")]
    [InlineData("C:/x")]
    [InlineData("trail/")]
    public void Escaping_subfolders_are_refused(string path) => Assert.NotNull(GitTemplateRules.CheckSubfolder(path));

    // ---- parser ----

    [Fact]
    public void A_valid_catalog_file_parses()
    {
        var result = Parse(Catalog(Entry(), Entry(version: "1.1.0")));

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("templates/starter", result.Entries[0].Path);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"templates\":[]}", "schemaVersion")]
    [InlineData("{\"schemaVersion\":1,\"templates\":[],\"extra\":1}", "$")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"templates\":[]}", "$")]
    [InlineData("{\"schemaVersion\":1,\"templates\":[],}", "$")]
    [InlineData("not json", "$")]
    public void A_malformed_catalog_file_is_an_error(string json, string path)
    {
        var result = Parse(json);

        Assert.False(result.IsValid);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path.StartsWith(path, StringComparison.Ordinal));
    }

    [Fact]
    public void Each_bad_key_of_an_entry_is_reported_by_path()
    {
        var result = Parse(Catalog(Entry(url: "http://example.test/r.git", commit: "main", path: "../x", hash: "zz", id: "Bad Id", version: "^1.0.0")));

        var paths = result.Issues.Select(issue => issue.Path).ToList();
        foreach (var key in new[] { "id", "version", "url", "commit", "path", "templateHash" })
        {
            Assert.Contains("templates[0]." + key, paths);
        }
    }

    [Fact]
    public void A_repeated_id_and_version_is_an_error() =>
        Assert.Contains(Parse(Catalog(Entry(), Entry())).Issues, issue => issue.Path == "templates[1]");

    [Fact]
    public void A_catalog_file_over_the_size_limit_is_refused() =>
        Assert.False(GitTemplateCatalogParser.Parse(new byte[GitTemplateCatalogParser.MaxBytes + 1], _defaultSchemes).IsValid);

    [Fact]
    public void A_credential_in_a_url_is_reported_without_echoing_it()
    {
        var result = Parse(Catalog(Entry(url: "https://u:" + _fakeToken + "@example.test/r.git")));

        Assert.False(result.IsValid);
        Assert.DoesNotContain(result.Issues, issue => issue.Message.Contains(_fakeToken, StringComparison.Ordinal) || issue.Path.Contains(_fakeToken, StringComparison.Ordinal));
    }

    // ---- catalog with a fake fetcher ----

    private sealed class FakeFetcher(Func<GitFetchRequest, GitFetchResult> behaviour) : IGitFetcher
    {
        public List<GitFetchRequest> Requests { get; } = [];

        public Task<GitFetchResult> FetchAsync(GitFetchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(behaviour(request));
        }
    }

    private static string NewTemp() => Path.Combine(Path.GetTempPath(), "bazaar-gittpl-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(dir, recursive: true);
    }

    private static GitTemplateEntry NewEntry(string path = "", string hash = "") => new()
    {
        Id = "starter",
        Version = "1.0.0",
        Url = "https://example.test/t.git",
        Commit = _commit,
        Path = path,
        TemplateHash = hash.Length == 0 ? _hash : hash,
    };

    [Fact]
    public async Task An_unlisted_id_or_version_is_not_held_and_nothing_is_fetched()
    {
        var fetcher = new FakeFetcher(_ => new GitFetchResult(GitFetchStatus.Ok, null));
        var catalog = new GitTemplateCatalog([NewEntry()], fetcher, new GitTemplateOptions { CacheRoot = NewTemp() });

        Assert.Null(await catalog.FindAsync("starter", "1.0.1", TestContext.Current.CancellationToken));
        Assert.Null(await catalog.FindAsync("other", "1.0.0", TestContext.Current.CancellationToken));
        Assert.Empty(fetcher.Requests);
        Assert.Equal(_hash, catalog.GetPinnedHash("starter", "1.0.0"));
        Assert.Null(catalog.GetPinnedHash("starter", "9.9.9"));
    }

    [Fact]
    public void A_catalog_built_from_an_invalid_entry_throws_without_echoing_the_url()
    {
        var bad = new GitTemplateEntry { Id = "starter", Version = "1.0.0", Url = "https://u:" + _fakeToken + "@example.test/r.git", Commit = _commit, TemplateHash = _hash };

        var ex = Assert.Throws<ArgumentException>(() => new GitTemplateCatalog([bad], new FakeFetcher(_ => new GitFetchResult(GitFetchStatus.Ok, null)), new GitTemplateOptions()));

        Assert.DoesNotContain(_fakeToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_fetch_throws_an_operator_safe_error_and_leaves_no_folder_behind()
    {
        var cache = NewTemp();
        try
        {
            var fetcher = new FakeFetcher(r =>
            {
                Directory.CreateDirectory(r.Destination);
                return new GitFetchResult(GitFetchStatus.FetchFailed, "git could not fetch the template repository.");
            });
            var catalog = new GitTemplateCatalog([NewEntry()], fetcher, new GitTemplateOptions { CacheRoot = cache });

            var ex = await Assert.ThrowsAsync<TemplateSourceException>(() => catalog.FindAsync("starter", "1.0.0", TestContext.Current.CancellationToken));

            Assert.DoesNotContain("example.test", ex.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
        }
        finally
        {
            Cleanup(cache);
        }
    }

    [Fact]
    public async Task A_fetched_checkout_is_cached_and_the_subfolder_is_the_source_root()
    {
        var cache = NewTemp();
        try
        {
            var fetcher = new FakeFetcher(r =>
            {
                Directory.CreateDirectory(Path.Combine(r.Destination, "templates", "starter"));
                File.WriteAllText(Path.Combine(r.Destination, "templates", "starter", "template.json"), "{}");
                return new GitFetchResult(GitFetchStatus.Ok, null);
            });
            var catalog = new GitTemplateCatalog([NewEntry("templates/starter")], fetcher, new GitTemplateOptions { CacheRoot = cache });

            var first = await catalog.FindAsync("starter", "1.0.0", TestContext.Current.CancellationToken);
            var second = await catalog.FindAsync("starter", "1.0.0", TestContext.Current.CancellationToken);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Single(fetcher.Requests);
            Assert.Equal(_commit, fetcher.Requests[0].Commit);
            var read = await first.ReadAsync("template.json", 1024, TestContext.Current.CancellationToken);
            Assert.Equal(TemplateFileStatus.Found, read.Status);
        }
        finally
        {
            Cleanup(cache);
        }
    }

    [Fact]
    public async Task A_missing_subfolder_is_an_operator_safe_error()
    {
        var cache = NewTemp();
        try
        {
            var fetcher = new FakeFetcher(r =>
            {
                Directory.CreateDirectory(r.Destination);
                return new GitFetchResult(GitFetchStatus.Ok, null);
            });
            var catalog = new GitTemplateCatalog([NewEntry("templates/none")], fetcher, new GitTemplateOptions { CacheRoot = cache });

            await Assert.ThrowsAsync<TemplateSourceException>(() => catalog.FindAsync("starter", "1.0.0", TestContext.Current.CancellationToken));
        }
        finally
        {
            Cleanup(cache);
        }
    }

    [Fact]
    public async Task The_resolver_reports_a_failed_fetch_as_a_company_source_error()
    {
        var cache = NewTemp();
        try
        {
            var fetcher = new FakeFetcher(_ => new GitFetchResult(GitFetchStatus.CommitNotFound, "the repository does not hold the pinned commit."));
            var catalog = new GitTemplateCatalog([NewEntry()], fetcher, new GitTemplateOptions { CacheRoot = cache });
            var plan = new SetupPlan { Company = new SetupCompanySection { Source = "template:starter@1.0.0" } };

            var result = await new TemplateResolver(catalog, RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

            Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
            Assert.Contains(result.Issues, issue => issue.Path == "company.source" && issue.Message.Contains("pinned commit", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(cache);
        }
    }

    // ---- fetcher refusals (no process runs) ----

    [Theory]
    [InlineData("http://example.test/r.git", "")]
    [InlineData("https://example.test/r.git", "main")]
    [InlineData("-x", "")]
    public async Task The_git_fetcher_refuses_a_bad_request_before_running_anything(string url, string commit)
    {
        var fetcher = new GitCliFetcher(new GitTemplateOptions { GitExecutable = "definitely-not-a-program" });

        var result = await fetcher.FetchAsync(new GitFetchRequest(url, commit.Length == 0 ? _commit : commit, NewTemp()), TestContext.Current.CancellationToken);

        Assert.Equal(GitFetchStatus.InvalidRequest, result.Status);
    }

    [Fact]
    public async Task The_git_fetcher_refuses_a_relative_or_non_empty_destination()
    {
        var dir = NewTemp();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "x"), "x");
            var fetcher = new GitCliFetcher(new GitTemplateOptions());

            var relative = await fetcher.FetchAsync(new GitFetchRequest("https://example.test/r.git", _commit, "relative/dir"), TestContext.Current.CancellationToken);
            var occupied = await fetcher.FetchAsync(new GitFetchRequest("https://example.test/r.git", _commit, dir), TestContext.Current.CancellationToken);

            Assert.Equal(GitFetchStatus.InvalidRequest, relative.Status);
            Assert.Equal(GitFetchStatus.InvalidRequest, occupied.Status);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task A_missing_git_program_is_reported_as_unavailable()
    {
        var dest = NewTemp();
        try
        {
            var fetcher = new GitCliFetcher(new GitTemplateOptions { GitExecutable = "definitely-not-a-program" });

            var result = await fetcher.FetchAsync(new GitFetchRequest("https://example.test/r.git", _commit, dest), TestContext.Current.CancellationToken);

            Assert.Equal(GitFetchStatus.GitUnavailable, result.Status);
        }
        finally
        {
            Cleanup(dest);
        }
    }

    // ---- real git on a local repository ----

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in _gitIdentity.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }

    /// <summary>Makes a repository holding the starter template under <c>templates/starter</c>. Returns its folder and the commit.</summary>
    private static (string Repo, string Commit) MakeRepo()
    {
        var repo = NewTemp();
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        var folder = Path.Combine(repo, "templates", "starter");
        Directory.CreateDirectory(Path.Combine(folder, "seeds", "charters"));
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(folder, "template.json"), TemplateFixtures.Manifest, utf8);
        File.WriteAllText(Path.Combine(folder, "seeds", "constitution.md"), TemplateFixtures.ConstitutionText, utf8);
        File.WriteAllText(Path.Combine(folder, "seeds", "charters", "reviewer.md"), TemplateFixtures.CharterText, utf8);
        File.WriteAllText(Path.Combine(folder, "seeds", "vault-layout.md"), TemplateFixtures.VaultText, utf8);
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "template");
        return (repo, Git(repo, "rev-parse", "HEAD"));
    }

    private static string FileUrl(string repo) => new Uri(repo).AbsoluteUri;

    private static GitTemplateOptions LocalOptions(string cache) => new() { AllowedSchemes = ["file"], CacheRoot = cache, Timeout = TimeSpan.FromSeconds(60) };

    [Fact]
    public async Task The_git_cli_fetches_a_pinned_commit_and_the_template_loads_with_the_pinned_hash()
    {
        Assert.SkipUnless(GitAvailable(), "git is not installed.");
        var (repo, commit) = MakeRepo();
        var cache = NewTemp();
        try
        {
            var expected = (await TemplateFixtures.Load(TemplateFixtures.Starter())).TemplateHash!;
            var options = LocalOptions(cache);
            var entry = new GitTemplateEntry { Id = "starter", Version = "1.0.0", Url = FileUrl(repo), Commit = commit, Path = "templates/starter", TemplateHash = expected };
            var catalog = new GitTemplateCatalog([entry], new GitCliFetcher(options), options);
            var plan = new SetupPlan { Company = new SetupCompanySection { Source = "template:starter@1.0.0" } };

            var result = await new TemplateResolver(catalog, RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

            Assert.Equal(SetupExitCode.Ok, result.ExitCode);
            Assert.Equal(expected, result.TemplateHash);
            Assert.Equal(3, result.Seeds.Count);
            // The .git folder is gone from the cache.
            Assert.DoesNotContain(Directory.EnumerateDirectories(cache, ".git", SearchOption.AllDirectories), _ => true);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(cache);
        }
    }

    [Fact]
    public async Task A_template_whose_content_differs_from_the_pinned_hash_is_refused()
    {
        Assert.SkipUnless(GitAvailable(), "git is not installed.");
        var (repo, commit) = MakeRepo();
        var cache = NewTemp();
        try
        {
            var options = LocalOptions(cache);
            var entry = new GitTemplateEntry { Id = "starter", Version = "1.0.0", Url = FileUrl(repo), Commit = commit, Path = "templates/starter", TemplateHash = _hash };
            var catalog = new GitTemplateCatalog([entry], new GitCliFetcher(options), options);
            var plan = new SetupPlan { Company = new SetupCompanySection { Source = "template:starter@1.0.0" } };

            var result = await new TemplateResolver(catalog, RegistrySetupSettingCatalog.Known).ResolveAsync(plan, TestContext.Current.CancellationToken);

            Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
            Assert.Null(result.Template);
            Assert.Null(result.TemplateHash);
            Assert.Empty(plan.Modules);
            Assert.Contains(result.Issues, issue => issue.Message.Contains("pinned", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(repo);
            Cleanup(cache);
        }
    }

    [Fact]
    public async Task A_commit_the_repository_does_not_hold_is_reported()
    {
        Assert.SkipUnless(GitAvailable(), "git is not installed.");
        var (repo, _) = MakeRepo();
        var dest = NewTemp();
        try
        {
            var fetcher = new GitCliFetcher(LocalOptions(NewTemp()));

            var result = await fetcher.FetchAsync(new GitFetchRequest(FileUrl(repo), new string('1', 40), dest), TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.DoesNotContain(repo, result.Detail ?? "", StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(dest);
        }
    }

    [Fact]
    public async Task The_pin_is_the_commit_so_a_later_commit_does_not_change_the_fetched_files()
    {
        Assert.SkipUnless(GitAvailable(), "git is not installed.");
        var (repo, commit) = MakeRepo();
        var dest = NewTemp();
        try
        {
            File.WriteAllText(Path.Combine(repo, "templates", "starter", "seeds", "vault-layout.md"), "changed later", new UTF8Encoding(false));
            Git(repo, "commit", "-q", "-a", "-m", "later");
            var fetcher = new GitCliFetcher(LocalOptions(NewTemp()));

            var result = await fetcher.FetchAsync(new GitFetchRequest(FileUrl(repo), commit, dest), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, result.Detail);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(dest, "templates", "starter", "seeds", "vault-layout.md"), TestContext.Current.CancellationToken);
            Assert.Equal(Encoding.UTF8.GetBytes(TemplateFixtures.VaultText), bytes);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(dest);
        }
    }
}
