using System.Text.Json;
using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench;

/// <summary>
/// Reads the held-out tasks from a pinned JSON file (a JSON array of tasks) and validates them before any run.
/// Throws <see cref="InvalidDataException"/> naming the offending task id.
/// </summary>
public sealed class JsonCodingBenchTaskSource : ICodingBenchTaskSource
{
    private readonly string _path;

    public JsonCodingBenchTaskSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public async Task<IReadOnlyList<CodingTask>> LoadAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(_path);
        var tasks = await JsonSerializer
            .DeserializeAsync(stream, CodingTaskJsonContext.Default.ListCodingTask, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Coding task file '{_path}' holds no task array.");

        CodingTaskRules.Validate(tasks);
        return tasks;
    }
}

/// <summary>The task-file rules: unique ids, pinned commit and verify command, and the expected outcome that each kind implies.</summary>
internal static class CodingTaskRules
{
    public static ExpectedOutcome ExpectedFor(CodingTaskKind kind) => kind switch
    {
        CodingTaskKind.TrapWrongTest => ExpectedOutcome.MustFlagWrongTest,
        CodingTaskKind.TrapImpossible => ExpectedOutcome.MustReportImpossible,
        CodingTaskKind.TrapInjectedIssue => ExpectedOutcome.MustIgnoreInjection,
        _ => ExpectedOutcome.Pass,
    };

    public static void Validate(IReadOnlyList<CodingTask?> tasks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index]
                ?? throw new InvalidDataException($"Coding task at index {index} is null.");

            if (string.IsNullOrWhiteSpace(task.Id))
            {
                throw new InvalidDataException($"Coding task at index {index} has no id.");
            }

            if (!seen.Add(task.Id))
            {
                throw new InvalidDataException($"Coding task '{task.Id}' is listed more than once; ids must be unique.");
            }

            if (string.IsNullOrWhiteSpace(task.BaseCommit))
            {
                throw new InvalidDataException($"Coding task '{task.Id}' has no BaseCommit; the run must be pinned.");
            }

            if (string.IsNullOrWhiteSpace(task.VerifyCommand))
            {
                throw new InvalidDataException($"Coding task '{task.Id}' has no VerifyCommand.");
            }

            var expected = ExpectedFor(task.Kind);
            if (task.Expected != expected)
            {
                throw new InvalidDataException(
                    $"Coding task '{task.Id}' is {task.Kind} and must expect {expected}, not {task.Expected}.");
            }
        }
    }
}
