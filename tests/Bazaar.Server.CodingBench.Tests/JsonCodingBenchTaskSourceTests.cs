using System.IO;
using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench.Tests;

public sealed class JsonCodingBenchTaskSourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "coding-bench-tests-" + Guid.NewGuid().ToString("N"));

    public JsonCodingBenchTaskSourceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private string Write(string json)
    {
        var path = Path.Combine(_directory, "tasks.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string Task(string id, string kind = "BugFix", string expected = "Pass", string baseCommit = "abc123", string verify = "dotnet test") =>
        $$"""
        {
          "id": "{{id}}",
          "kind": "{{kind}}",
          "title": "Title {{id}}",
          "prompt": "Do it.",
          "baseCommit": "{{baseCommit}}",
          "verifyCommand": "{{verify}}",
          "expected": "{{expected}}",
          "heldOut": true
        }
        """;

    private static string Array(params string[] tasks) => "[" + string.Join(",", tasks) + "]";

    [Fact]
    public async Task Loads_a_valid_pinned_file()
    {
        var path = Write(Array(
            Task("task-a"),
            Task("task-b", kind: "TrapWrongTest", expected: "MustFlagWrongTest")));

        var tasks = await new JsonCodingBenchTaskSource(path).LoadAsync(CancellationToken.None);

        Assert.Equal(2, tasks.Count);
        Assert.Equal("task-a", tasks[0].Id);
        Assert.Equal(CodingTaskKind.TrapWrongTest, tasks[1].Kind);
        Assert.Equal(ExpectedOutcome.MustFlagWrongTest, tasks[1].Expected);
        Assert.Equal("abc123", tasks[0].BaseCommit);
        Assert.True(tasks[0].HeldOut);
    }

    [Fact]
    public async Task Duplicate_ids_are_rejected_with_the_id()
    {
        var path = Write(Array(Task("task-a"), Task("task-a")));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JsonCodingBenchTaskSource(path).LoadAsync(CancellationToken.None));

        Assert.Contains("task-a", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_base_commit_is_rejected_with_the_id()
    {
        var path = Write(Array(Task("task-c", baseCommit: "")));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JsonCodingBenchTaskSource(path).LoadAsync(CancellationToken.None));

        Assert.Contains("task-c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_verify_command_is_rejected_with_the_id()
    {
        var path = Write(Array(Task("task-d", verify: "  ")));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JsonCodingBenchTaskSource(path).LoadAsync(CancellationToken.None));

        Assert.Contains("task-d", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TrapWrongTest", "Pass")]
    [InlineData("TrapImpossible", "Pass")]
    [InlineData("TrapInjectedIssue", "MustFlagWrongTest")]
    [InlineData("BugFix", "MustReportImpossible")]
    [InlineData("Refactor", "MustIgnoreInjection")]
    public async Task Expected_outcome_must_match_the_kind(string kind, string expected)
    {
        var path = Write(Array(Task("task-e", kind: kind, expected: expected)));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JsonCodingBenchTaskSource(path).LoadAsync(CancellationToken.None));

        Assert.Contains("task-e", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_trap_kind_accepts_its_own_expected_outcome()
    {
        var path = Write(Array(
            Task("t1", kind: "TrapWrongTest", expected: "MustFlagWrongTest"),
            Task("t2", kind: "TrapImpossible", expected: "MustReportImpossible"),
            Task("t3", kind: "TrapInjectedIssue", expected: "MustIgnoreInjection"),
            Task("t4", kind: "GodotCheck", expected: "Pass")));

        var tasks = await new JsonCodingBenchTaskSource(path).LoadAsync(CancellationToken.None);

        Assert.Equal(4, tasks.Count);
    }

    [Fact]
    public async Task Static_source_returns_the_list_it_was_given()
    {
        var list = new List<CodingTask>
        {
            new("x", CodingTaskKind.BugFix, "X", "p", "c", "v", ExpectedOutcome.Pass),
        };

        var loaded = await new StaticCodingBenchTaskSource(list).LoadAsync(CancellationToken.None);

        Assert.Single(loaded);
        Assert.Equal("x", loaded[0].Id);
    }
}
