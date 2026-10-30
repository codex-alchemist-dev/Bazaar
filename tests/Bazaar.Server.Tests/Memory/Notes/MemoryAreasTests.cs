using Bazaar.Server.Memory.Notes;

namespace Bazaar.Server.Tests.Memory.Notes;

public class MemoryAreasTests
{
    private const string Ulid = "01J9ZABCDEFGHJKMNPQRSTVWXY";

    [Theory]
    [InlineData(MemoryKind.Episodic, "agents/alpha/memory/episodes")]
    [InlineData(MemoryKind.Semantic, "agents/alpha/memory/semantic")]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/lessons")]
    [InlineData(MemoryKind.Failure, "agents/alpha/memory/failures")]
    [InlineData(MemoryKind.Preference, "agents/alpha/memory/preferences")]
    [InlineData(MemoryKind.Working, "agents/alpha/memory/working")]
    public void Expected_private_area_per_kind(MemoryKind kind, string expected) =>
        Assert.Equal(expected, MemoryAreas.ExpectedDirectory(kind, MemoryScope.Private, "alpha"));

    [Fact]
    public void Project_and_company_areas_follow_the_same_folders()
    {
        Assert.Equal("projects/example/memory/semantic", MemoryAreas.ExpectedDirectory(MemoryKind.Semantic, MemoryScope.Project, "example"));
        Assert.Equal("company/memory/lessons", MemoryAreas.ExpectedDirectory(MemoryKind.Procedural, MemoryScope.Company));
        Assert.Equal("hive/procedure", MemoryAreas.ExpectedDirectory(MemoryKind.Hive, MemoryScope.Hive, hiveKind: "procedure"));
    }

    [Theory]
    [InlineData(MemoryKind.Hive, MemoryScope.Private)]
    [InlineData(MemoryKind.Procedural, MemoryScope.Hive)]
    [InlineData(MemoryKind.Hive, MemoryScope.Company)]
    public void Kind_and_scope_combinations_without_an_area_have_no_expected_directory(MemoryKind kind, MemoryScope scope) =>
        Assert.Null(MemoryAreas.ExpectedDirectory(kind, scope, "alpha", hiveKind: null));

    [Theory]
    [InlineData("hive/secrets")]
    [InlineData("hive/Procedure")]
    public void Hive_kinds_outside_the_allowed_list_are_refused(string dir) =>
        Assert.Null(MemoryAreas.ExpectedDirectory(MemoryKind.Hive, MemoryScope.Hive, hiveKind: dir[5..]));

    [Theory]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/lessons/retry-backoff.md")]
    [InlineData(MemoryKind.Failure, "agents/alpha/memory/failures/timeouts.md")]
    [InlineData(MemoryKind.Working, "agents/alpha/memory/working/task-42.md")]
    [InlineData(MemoryKind.Episodic, "agents/alpha/memory/episodes/2026-10-30/" + Ulid + ".md")]
    [InlineData(MemoryKind.Semantic, "projects/example/memory/semantic/facts.md")]
    [InlineData(MemoryKind.Preference, "company/memory/preferences/tone.md")]
    [InlineData(MemoryKind.Hive, "hive/procedure/retry-backoff.md")]
    public void Valid_paths_are_accepted_for_their_kind(MemoryKind kind, string path)
    {
        var check = MemoryAreas.CheckPath(path, kind);

        Assert.True(check.IsValid, check.Reason);
    }

    [Fact]
    public void Scope_owner_and_hive_kind_are_reported()
    {
        var project = MemoryAreas.CheckPath("projects/example/memory/semantic/facts.md", MemoryKind.Semantic);
        var hive = MemoryAreas.CheckPath("hive/procedure/retry.md", MemoryKind.Hive);

        Assert.Equal(MemoryScope.Project, project.Scope);
        Assert.Equal("example", project.Owner);
        Assert.Equal(MemoryScope.Hive, hive.Scope);
        Assert.Equal("procedure", hive.HiveKind);
    }

    [Theory]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/failures/timeouts.md", "wrong-area")]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/lessons/nested/x.md", "wrong-area")]
    [InlineData(MemoryKind.Episodic, "agents/alpha/memory/episodes/" + Ulid + ".md", "wrong-area")]
    [InlineData(MemoryKind.Episodic, "agents/alpha/memory/episodes/2026-13-40/" + Ulid + ".md", "wrong-area")]
    [InlineData(MemoryKind.Episodic, "agents/alpha/memory/episodes/2026-10-30/short.md", "wrong-area")]
    [InlineData(MemoryKind.Semantic, "agents/alpha/memory/lessons/x.md", "wrong-area")]
    [InlineData(MemoryKind.Hive, "agents/alpha/memory/lessons/x.md", "kind-not-allowed-in-area")]
    [InlineData(MemoryKind.Procedural, "hive/procedure/x.md", "kind-not-allowed-in-area")]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/lessons/../../../secret.md", "unsafe-path")]
    [InlineData(MemoryKind.Procedural, "/agents/alpha/memory/lessons/x.md", "unsafe-path")]
    [InlineData(MemoryKind.Procedural, "agents\\alpha\\memory\\lessons\\x.md", "unsafe-path")]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/lessons/x.txt", "unsafe-path")]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/lessons/x\u0001.md", "unsafe-path")]
    [InlineData(MemoryKind.Procedural, "agents/Alpha/memory/lessons/x.md", "bad-owner")]
    [InlineData(MemoryKind.Procedural, ".md", "outside-memory-areas")]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/lessons/Retry.md", "bad-file-name")]
    [InlineData(MemoryKind.Procedural, "agents/alpha/memory/lessons/.md", "bad-file-name")]
    [InlineData(MemoryKind.Procedural, "vault/notes/x.md", "outside-memory-areas")]
    [InlineData(MemoryKind.Procedural, "agents/alpha/notes/lessons/x.md", "outside-memory-areas")]
    [InlineData(MemoryKind.Procedural, "", "path-length")]
    public void Paths_outside_the_rules_are_refused(MemoryKind kind, string path, string reason)
    {
        var check = MemoryAreas.CheckPath(path, kind);

        Assert.False(check.IsValid);
        Assert.Equal(reason, check.Reason);
    }

    [Fact]
    public void Paths_longer_than_the_limit_are_refused()
    {
        var path = "agents/alpha/memory/lessons/" + new string('a', MemoryAreas.MaxPathLength) + ".md";

        Assert.Equal("path-length", MemoryAreas.CheckPath(path, MemoryKind.Procedural).Reason);
    }
}
