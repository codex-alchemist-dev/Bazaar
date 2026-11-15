using System.Text.Json;

namespace Bazaar.Testing.LiveEval;

/// <summary>One validation problem, located by a JSON-style field path such as <c>cases[2].prompt</c>.</summary>
public sealed record TaskSetIssue(string FieldPath, string Message)
{
    public override string ToString() => $"{FieldPath}: {Message}";
}

/// <summary>Thrown when a task-set document is malformed. <see cref="Issues"/> lists every problem found.</summary>
public sealed class TaskSetValidationException(IReadOnlyList<TaskSetIssue> issues)
    : Exception($"Task set is invalid ({issues.Count} issue(s)): " + string.Join("; ", issues))
{
    public IReadOnlyList<TaskSetIssue> Issues { get; } = issues;
}

/// <summary>
/// Reads and validates a task-set JSON document. Every problem is collected before throwing, so one run reports them all.
/// Unknown properties are errors, so a misspelt key cannot silently drop a check.
/// </summary>
public static class TaskSetLoader
{
    private static readonly Dictionary<string, TaskCheckKind> _checkKindNames = new(StringComparer.Ordinal)
    {
        ["build"] = TaskCheckKind.Build,
        ["tests"] = TaskCheckKind.Tests,
        ["test-files-untouched"] = TaskCheckKind.TestFilesUntouched,
        ["format-compliant"] = TaskCheckKind.FormatCompliant,
        ["fake-success"] = TaskCheckKind.FakeSuccess,
        ["must-flag"] = TaskCheckKind.MustFlag,
        ["must-report-failure"] = TaskCheckKind.MustReportFailure,
        ["must-not-act"] = TaskCheckKind.MustNotAct,
    };

    private static readonly JsonDocumentOptions _parseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>The wire name of each check kind, for example <c>test-files-untouched</c>.</summary>
    public static string ToWireName(TaskCheckKind kind) =>
        _checkKindNames.First(pair => pair.Value == kind).Key;

    /// <summary>Parses and validates a task set. Throws <see cref="TaskSetValidationException"/> on any problem.</summary>
    public static TaskSet Load(string json)
    {
        var issues = new List<TaskSetIssue>();
        var set = TryLoad(json, issues);
        if (set is null || issues.Count > 0)
        {
            throw new TaskSetValidationException(issues);
        }

        return set;
    }

    private static TaskSet? TryLoad(string json, List<TaskSetIssue> issues)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, _parseOptions);
        }
        catch (JsonException ex)
        {
            issues.Add(new TaskSetIssue("$", $"not valid JSON ({ex.Message})"));
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new TaskSetIssue("$", "must be a JSON object"));
                return null;
            }

            RejectUnknown(root, "$", ["id", "version", "cases"], issues);

            var id = RequiredText(root, "$", "id", issues);
            var version = RequiredVersion(root, issues);
            var cases = ReadCases(root, issues);
            return id is null || version is null || cases is null ? null : new TaskSet(id, version.Value, cases);
        }
    }

    private static List<TaskCase>? ReadCases(JsonElement root, List<TaskSetIssue> issues)
    {
        if (!root.TryGetProperty("cases", out var casesElement))
        {
            issues.Add(new TaskSetIssue("cases", "is required"));
            return null;
        }

        if (casesElement.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new TaskSetIssue("cases", "must be an array"));
            return null;
        }

        if (casesElement.GetArrayLength() == 0)
        {
            issues.Add(new TaskSetIssue("cases", "must contain at least one case"));
            return null;
        }

        var cases = new List<TaskCase>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var element in casesElement.EnumerateArray())
        {
            var path = $"cases[{index}]";
            index++;
            var taskCase = ReadCase(element, path, issues);
            if (taskCase is null)
            {
                continue;
            }

            if (!seenIds.Add(taskCase.Id))
            {
                issues.Add(new TaskSetIssue($"{path}.id", $"duplicate case id '{taskCase.Id}'"));
                continue;
            }

            cases.Add(taskCase);
        }

        return cases;
    }

    private static TaskCase? ReadCase(JsonElement element, string path, List<TaskSetIssue> issues)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new TaskSetIssue(path, "must be a JSON object"));
            return null;
        }

        RejectUnknown(element, path, ["id", "prompt", "checks"], issues);

        var id = RequiredText(element, path, "id", issues);
        var prompt = RequiredText(element, path, "prompt", issues);
        var checks = ReadChecks(element, path, issues);
        return id is null || prompt is null || checks is null ? null : new TaskCase(id, prompt, checks);
    }

    private static List<TaskCheck>? ReadChecks(JsonElement element, string path, List<TaskSetIssue> issues)
    {
        var checksPath = $"{path}.checks";
        if (!element.TryGetProperty("checks", out var checksElement))
        {
            issues.Add(new TaskSetIssue(checksPath, "is required"));
            return null;
        }

        if (checksElement.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new TaskSetIssue(checksPath, "must be an array"));
            return null;
        }

        if (checksElement.GetArrayLength() == 0)
        {
            issues.Add(new TaskSetIssue(checksPath, "must contain at least one check"));
            return null;
        }

        var checks = new List<TaskCheck>();
        var index = 0;
        var ok = true;
        foreach (var item in checksElement.EnumerateArray())
        {
            var itemPath = $"{checksPath}[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.String)
            {
                issues.Add(new TaskSetIssue(itemPath, "must be a check name string"));
                ok = false;
                continue;
            }

            var name = item.GetString() ?? string.Empty;
            if (!_checkKindNames.TryGetValue(name, out var kind))
            {
                issues.Add(new TaskSetIssue(itemPath, $"unknown check kind '{name}'"));
                ok = false;
                continue;
            }

            checks.Add(new TaskCheck(kind));
        }

        return ok ? checks : null;
    }

    private static string? RequiredText(JsonElement element, string path, string name, List<TaskSetIssue> issues)
    {
        var fieldPath = $"{path}.{name}";
        if (path == "$")
        {
            fieldPath = name;
        }

        if (!element.TryGetProperty(name, out var value))
        {
            issues.Add(new TaskSetIssue(fieldPath, "is required"));
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            issues.Add(new TaskSetIssue(fieldPath, "must be a string"));
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            issues.Add(new TaskSetIssue(fieldPath, "must not be empty"));
            return null;
        }

        return text;
    }

    private static int? RequiredVersion(JsonElement root, List<TaskSetIssue> issues)
    {
        if (!root.TryGetProperty("version", out var value))
        {
            issues.Add(new TaskSetIssue("version", "is required"));
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version) || version < 1)
        {
            issues.Add(new TaskSetIssue("version", "must be an integer of at least 1"));
            return null;
        }

        return version;
    }

    private static void RejectUnknown(JsonElement element, string path, string[] allowed, List<TaskSetIssue> issues)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                var fieldPath = path == "$" ? property.Name : $"{path}.{property.Name}";
                issues.Add(new TaskSetIssue(fieldPath, "unknown property"));
            }
        }
    }
}
