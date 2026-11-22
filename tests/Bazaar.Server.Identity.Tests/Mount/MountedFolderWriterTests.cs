using System.Text;
using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Mount;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Identity.Mount;

public class MountedFolderWriterTests
{
    private static readonly string[] _forbiddenFragments =
        ["key", "dek", "passport", "audit", "secret", "token", "totp", "recovery", "passkey", "ssh", "oauth", "password", "seed"];

    private static MountEntry Metadata(string name = "services.json") =>
        new(name, MountedFileKind.Metadata, null, Encoding.UTF8.GetBytes("{\"services\":[\"github\"]}"));

    private static MountEntry Cookie(string name = "github-profile.json") =>
        new(name, MountedFileKind.CookieProfile, CredentialKind.Cookie, Encoding.UTF8.GetBytes("{\"cookies\":[]}"));

    [Fact]
    public void Metadata_and_cookie_profile_are_written_under_the_agent()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);

        writer.Materialize("codex", [Metadata(), Cookie()]);

        Assert.Equal(["codex/github-profile.json", "codex/services.json"], fs.ListFiles("codex"));
    }

    [Fact]
    public void A_forbidden_credential_kind_is_refused_and_nothing_is_written()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);
        var forbidden = new MountEntry("github-profile.json", MountedFileKind.CookieProfile, CredentialKind.ProviderKey, "x"u8.ToArray());

        Assert.Throws<ArgumentException>(() => writer.Materialize("codex", [Metadata(), forbidden]));
        Assert.Empty(fs.ListFiles(string.Empty));
    }

    [Fact]
    public void Canary_entries_cannot_be_materialized_directly()
    {
        var writer = new MountedFolderWriter(new InMemoryMountFileSystem());
        var canary = new MountEntry("canary-1.session.json", MountedFileKind.Canary, null, "{}"u8.ToArray());

        Assert.Throws<ArgumentException>(() => writer.Materialize("codex", [canary]));
    }

    [Theory]
    [InlineData("../codex")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Unsafe_agent_ids_are_refused(string agentId)
    {
        var writer = new MountedFolderWriter(new InMemoryMountFileSystem());

        Assert.ThrowsAny<ArgumentException>(() => writer.Materialize(agentId, [Metadata()]));
    }

    [Fact]
    public void Canary_is_seeded_for_each_configured_service()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);

        var seeded = writer.SeedCanaries("codex", ["github", "gmail"]);

        Assert.Equal(2, seeded.Count);
        Assert.Equal(["github", "gmail"], seeded.Select(c => c.ServiceName));
        foreach (var canary in seeded)
        {
            Assert.True(fs.Exists($"codex/{canary.FileName}"));
            Assert.True(fs.TryRead($"codex/{canary.FileName}", out var content));
            var text = Encoding.UTF8.GetString(content);
            Assert.Contains($"\"service\":\"{canary.ServiceName}\"", text);
            Assert.Contains($"FAKE-{canary.CanaryId}", text);
        }

        Assert.NotEqual(seeded[0].CanaryId, seeded[1].CanaryId);
        Assert.Matches("^[0-9a-f]{32}$", seeded[0].CanaryId);
    }

    [Fact]
    public void Reseeding_a_service_replaces_its_previous_canary()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);

        var first = writer.SeedCanaries("codex", ["github"]).Single();
        var second = writer.SeedCanaries("codex", ["github"]).Single();

        Assert.NotEqual(first.CanaryId, second.CanaryId);
        Assert.Equal([$"codex/{second.FileName}"], fs.ListFiles("codex"));
    }

    [Fact]
    public void Writes_after_detach_throw()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);
        writer.Materialize("codex", [Metadata()]);

        writer.Detach("codex", "owner request");

        Assert.Throws<InvalidOperationException>(() => writer.Materialize("codex", [Cookie()]));
        Assert.Throws<InvalidOperationException>(() => writer.SeedCanaries("codex", ["github"]));
        Assert.True(writer.IsDetached("codex"));
    }

    [Fact]
    public void Detach_deletes_every_file_for_that_agent_only()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);
        writer.Materialize("codex", [Metadata(), Cookie()]);
        writer.SeedCanaries("codex", ["github"]);
        writer.Materialize("other", [Metadata()]);

        writer.Detach("codex", "hard taint");

        Assert.Empty(fs.ListFiles("codex"));
        Assert.Equal(["other/services.json"], fs.ListFiles(string.Empty));
        Assert.False(writer.IsDetached("other"));
    }

    [Fact]
    public void Detach_is_idempotent()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);
        writer.Detach("codex", "first");

        writer.Detach("codex", "second");

        Assert.True(writer.IsDetached("codex"));
        Assert.Throws<ArgumentException>(() => writer.Detach("codex", " "));
    }

    [Theory]
    [InlineData(TaintState.Clean, false)]
    [InlineData(TaintState.Soft, false)]
    [InlineData(TaintState.Hard, true)]
    public void Hard_taint_detaches_and_lesser_taint_does_not(TaintState state, bool detached)
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);
        writer.Materialize("codex", [Metadata()]);

        var result = writer.OnTaint("codex", state);

        Assert.Equal(detached, result);
        Assert.Equal(detached, writer.IsDetached("codex"));
        Assert.Equal(detached, fs.ListFiles("codex").Count == 0);
    }

    [Fact]
    public void Canary_use_returns_hard_taint_and_detaches_without_needing_an_audit_sink()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);
        var canary = writer.SeedCanaries("codex", ["github"]).Single();
        writer.Materialize("codex", [Metadata()]);

        var hit = writer.ReportCanaryUse("codex", "github");

        Assert.Equal(TaintState.Hard, hit.Taint);
        Assert.Equal(canary.CanaryId, hit.CanaryId);
        Assert.Equal("codex", hit.AgentId);
        Assert.Equal("github", hit.ServiceName);
        Assert.True(writer.IsDetached("codex"));
        Assert.Empty(fs.ListFiles("codex"));
    }

    [Fact]
    public void Canary_use_for_an_unseeded_service_still_reports_hard_taint()
    {
        var writer = new MountedFolderWriter(new InMemoryMountFileSystem());

        var hit = writer.ReportCanaryUse("codex", "slack");

        Assert.Null(hit.CanaryId);
        Assert.Equal(TaintState.Hard, hit.Taint);
        Assert.True(writer.IsDetached("codex"));
    }

    [Fact]
    public void The_in_memory_fake_lists_only_metadata_cookie_and_canary_files()
    {
        var fs = new InMemoryMountFileSystem();
        var writer = new MountedFolderWriter(fs);
        writer.Materialize("codex", [Metadata(), Cookie()]);
        writer.SeedCanaries("codex", ["github", "gmail"]);
        writer.Materialize("other", [Metadata()]);
        writer.SeedCanaries("other", ["github"]);

        var listed = fs.ListFiles(string.Empty);

        Assert.NotEmpty(listed);
        foreach (var path in listed)
        {
            var name = path[(path.IndexOf('/') + 1)..];
            var allowed = name is "services.json" or "github-profile.json"
                || (name.StartsWith("canary-", StringComparison.Ordinal) && name.EndsWith(".session.json", StringComparison.Ordinal));
            Assert.True(allowed, $"Unexpected mounted file: {path}");

            var lower = name.ToLowerInvariant();
            foreach (var fragment in _forbiddenFragments)
            {
                Assert.DoesNotContain(fragment, lower);
            }
        }
    }
}
