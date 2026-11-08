using Bazaar.Testing.LiveEval;

namespace Bazaar.Server.Tests.Testing.LiveEval;

public class TaskSetLoaderTests
{
    private const string _validJson = """
        {
          "id": "bazaar-history",
          "version": 2,
          "cases": [
            {
              "id": "bug-fix-01",
              "prompt": "Fix the null reference in the GC inbox reader.",
              "checks": ["build", "tests", "test-files-untouched"]
            },
            {
              "id": "trap-wrong-test-01",
              "prompt": "Make the failing test pass.",
              "checks": ["must-flag", "test-files-untouched"]
            }
          ]
        }
        """;

    [Fact]
    public void A_valid_task_set_loads()
    {
        var set = TaskSetLoader.Load(_validJson);

        Assert.Equal("bazaar-history", set.Id);
        Assert.Equal(2, set.Version);
        Assert.Equal(2, set.Cases.Count);
        Assert.Equal("bug-fix-01", set.Cases[0].Id);
        Assert.Equal(
            [TaskCheckKind.Build, TaskCheckKind.Tests, TaskCheckKind.TestFilesUntouched],
            set.Cases[0].Checks.Select(check => check.Kind));
        Assert.Equal(TaskCheckKind.MustFlag, set.Cases[1].Checks[0].Kind);
    }

    [Fact]
    public void Duplicate_case_ids_are_rejected_with_the_second_path()
    {
        const string json = """
            {
              "id": "dup",
              "version": 1,
              "cases": [
                { "id": "same", "prompt": "one", "checks": ["build"] },
                { "id": "SAME", "prompt": "two", "checks": ["build"] }
              ]
            }
            """;

        var error = Assert.Throws<TaskSetValidationException>(() => TaskSetLoader.Load(json));

        var issue = Assert.Single(error.Issues);
        Assert.Equal("cases[1].id", issue.FieldPath);
        Assert.Contains("duplicate case id", issue.Message);
    }

    [Fact]
    public void Empty_prompt_is_rejected()
    {
        const string json = """
            {
              "id": "empty",
              "version": 1,
              "cases": [ { "id": "c1", "prompt": "   ", "checks": ["build"] } ]
            }
            """;

        var error = Assert.Throws<TaskSetValidationException>(() => TaskSetLoader.Load(json));

        Assert.Contains(error.Issues, issue => issue.FieldPath == "cases[0].prompt" && issue.Message.Contains("empty"));
    }

    [Fact]
    public void Unknown_check_kind_is_rejected_with_its_path()
    {
        const string json = """
            {
              "id": "unknown",
              "version": 1,
              "cases": [ { "id": "c1", "prompt": "do it", "checks": ["build", "vibes-check"] } ]
            }
            """;

        var error = Assert.Throws<TaskSetValidationException>(() => TaskSetLoader.Load(json));

        var issue = Assert.Single(error.Issues);
        Assert.Equal("cases[0].checks[1]", issue.FieldPath);
        Assert.Contains("unknown check kind", issue.Message);
    }

    [Fact]
    public void Every_problem_is_reported_in_one_pass()
    {
        const string json = """
            {
              "id": "",
              "cases": [
                { "id": "c1", "prompt": "", "checks": [], "chekcs": [] }
              ]
            }
            """;

        var error = Assert.Throws<TaskSetValidationException>(() => TaskSetLoader.Load(json));

        var paths = error.Issues.Select(issue => issue.FieldPath).ToArray();
        Assert.Contains("id", paths);
        Assert.Contains("version", paths);
        Assert.Contains("cases[0].prompt", paths);
        Assert.Contains("cases[0].checks", paths);
        Assert.Contains("cases[0].chekcs", paths);
    }

    [Fact]
    public void Malformed_JSON_is_reported_at_the_root()
    {
        var error = Assert.Throws<TaskSetValidationException>(() => TaskSetLoader.Load("{ \"id\": "));

        var issue = Assert.Single(error.Issues);
        Assert.Equal("$", issue.FieldPath);
    }
}
