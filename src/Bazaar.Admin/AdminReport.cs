using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Admin;

/// <summary>
/// One command's result. The plain text and the <c>--json</c> document are two renderings of this one model, so they cannot
/// disagree. Values are never stored here for a secret-typed key: <see cref="SetupChangeView"/> hides them before this model is built.
/// </summary>
public sealed class AdminReport
{
    /// <summary>validate, dry-run, apply or usage (an argument error, before any file was read).</summary>
    public string Command { get; init; } = "";

    /// <summary>The exit code, from <see cref="Bazaar.Server.Setup.SetupExitCode"/>.</summary>
    public int ExitCode { get; init; }

    /// <summary>valid, invalid-input, held, applied or apply-failed-rolled-back.</summary>
    public string Result { get; init; } = "";

    /// <summary>The plan hash. Null when the file has errors, because no plan was built.</summary>
    public string? PlanHash { get; init; }

    /// <summary>empty for a dry run or apply, which compare against an empty snapshot. Null for validate.</summary>
    public string? CurrentState { get; init; }

    /// <summary>Errors and holds, each with the key path and the rule. Never a value.</summary>
    public List<AdminIssue> Issues { get; init; } = [];

    /// <summary>Dry-run changes, sorted by path.</summary>
    public List<AdminChange> Changes { get; init; } = [];

    /// <summary>Steps that completed, in run order.</summary>
    public List<string> AppliedSteps { get; init; } = [];
}

/// <summary>One error or hold. <see cref="Path"/> is a dotted key path, or a flag name for a usage error.</summary>
public sealed class AdminIssue
{
    /// <summary>error or hold.</summary>
    public string Severity { get; init; } = "";

    public string Path { get; init; } = "";

    public string Message { get; init; } = "";
}

/// <summary>One dry-run change. <see cref="Current"/> and <see cref="Planned"/> are display text, with secrets hidden.</summary>
public sealed class AdminChange
{
    public string Path { get; init; } = "";

    /// <summary>create, update or remove.</summary>
    public string Kind { get; init; } = "";

    public string? Current { get; init; }

    public string? Planned { get; init; }
}

/// <summary>The JSON document for <c>--json</c>. Source-generated, so the output is AOT-safe and uses no reflection.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = true)]
[JsonSerializable(typeof(AdminReport))]
[JsonSerializable(typeof(AdminIssue))]
[JsonSerializable(typeof(AdminChange))]
public sealed partial class AdminJsonContext : JsonSerializerContext
{
}
