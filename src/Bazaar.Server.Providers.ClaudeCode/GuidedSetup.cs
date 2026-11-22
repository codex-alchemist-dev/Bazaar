using System.Collections.Concurrent;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>The steps of Claude Code guided setup (docs/design/91 section 14, step 7), in order.</summary>
public enum ClaudeCodeSetupStep
{
    Terms,
    Binary,
    LoginFolder,
    LoginOwner,
    Settings,
}

public enum ClaudeCodeStepStatus
{
    Ok,
    Failed,
    Skipped,
}

public sealed record ClaudeCodeStepResult(ClaudeCodeSetupStep Step, ClaudeCodeStepStatus Status, string Message);

/// <summary>What the operator filled in. Nothing here is a credential.</summary>
public sealed record ClaudeCodeSetupRequest
{
    /// <summary>True when the operator accepted the terms summary shown by <see cref="ClaudeCodeTermsSummary.Points"/>.</summary>
    public bool AcceptedTerms { get; init; }

    /// <summary>Path of the <c>claude</c> program. Empty means look it up on PATH.</summary>
    public string BinaryPath { get; init; } = "";

    /// <summary>The Claude configuration folder the operator signed in to (login mode).</summary>
    public string ConfigDir { get; init; } = "";

    /// <summary>The one human whose login this is, as <c>user:&lt;id&gt;</c>.</summary>
    public string LoginOwner { get; init; } = "";

    public IReadOnlyList<string> Models { get; init; } = [];
}

/// <summary>The report: the steps, and the settings to apply once every step is Ok (null until then).</summary>
public sealed record ClaudeCodeSetupReport(IReadOnlyList<ClaudeCodeStepResult> Steps, ClaudeCodeOptions? Settings)
{
    public bool Ready => Settings is not null;
}

/// <summary>The terms summary the operator accepts (docs/design/91 section 2, rules T6 and T10). Fixed text, shown as is.</summary>
public static class ClaudeCodeTermsSummary
{
    public static IReadOnlyList<string> Points { get; } =
    [
        "Bazaar runs the unmodified claude program. You sign in to it yourself, in your own terminal; Bazaar never sees or stores the login.",
        "One login serves one person. Do not share it with other people.",
        "Running agents all day is not ordinary, individual use. Anthropic may limit or suspend the account without notice.",
        "Hosting the program in a product requires agreeing to Anthropic's Commercial Terms.",
    ];
}

/// <summary>Keeps the operator's terms acceptance with its time (rule T6).</summary>
public interface IClaudeCodeAcknowledgementStore
{
    DateTimeOffset? GetAcknowledgedAt();

    void Record(DateTimeOffset acknowledgedAt);
}

/// <summary>Process-lifetime store. The durable one rides the admin settings (doc 51) and replaces this.</summary>
public sealed class InMemoryClaudeCodeAcknowledgementStore : IClaudeCodeAcknowledgementStore
{
    private readonly ConcurrentQueue<DateTimeOffset> _at = new();

    public DateTimeOffset? GetAcknowledgedAt() => _at.TryPeek(out var v) ? v : null;

    public void Record(DateTimeOffset acknowledgedAt)
    {
        if (_at.IsEmpty)
        {
            _at.Enqueue(acknowledgedAt);
        }
    }
}

/// <summary>
/// Claude Code guided setup, run as data for the admin panel. It checks only that things exist (the program, the login folder)
/// and never opens a file inside the login folder (rule T2). It stops at the first failing step; later steps are Skipped.
/// </summary>
public sealed class ClaudeCodeGuidedSetup(IClaudeCodeAcknowledgementStore acknowledgements, TimeProvider? time = null, string? pathVariable = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly string _path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "";

    /// <summary>The command the operator runs in their own terminal to sign in (rule T4).</summary>
    public static string SignInCommand(string configDir) =>
        OperatingSystem.IsWindows()
            ? $"set CLAUDE_CONFIG_DIR={configDir} && claude"
            : $"CLAUDE_CONFIG_DIR={configDir} claude";

    public ClaudeCodeSetupReport Run(ClaudeCodeSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var steps = new List<ClaudeCodeStepResult>();

        if (request.AcceptedTerms)
        {
            acknowledgements.Record(_time.GetUtcNow());
        }

        var acknowledgedAt = acknowledgements.GetAcknowledgedAt();
        if (!Add(steps, acknowledgedAt is null
                ? Fail(ClaudeCodeSetupStep.Terms, "The terms summary has not been accepted. Setup stops until it is.")
                : Ok(ClaudeCodeSetupStep.Terms, "The terms summary is accepted.")))
        {
            return Stop(steps);
        }

        var binary = ResolveBinary(request.BinaryPath);
        if (!Add(steps, binary is null
                ? Fail(ClaudeCodeSetupStep.Binary, "The claude program was not found. Install it, or give its full path.")
                : Ok(ClaudeCodeSetupStep.Binary, "The claude program was found.")))
        {
            return Stop(steps);
        }

        if (!Add(steps, string.IsNullOrWhiteSpace(request.ConfigDir) || !Directory.Exists(request.ConfigDir)
                ? Fail(ClaudeCodeSetupStep.LoginFolder, "The login folder does not exist. Sign in with: " + SignInCommand(string.IsNullOrWhiteSpace(request.ConfigDir) ? "<folder>" : request.ConfigDir))
                : Ok(ClaudeCodeSetupStep.LoginFolder, "The login folder exists. Its contents are not read.")))
        {
            return Stop(steps);
        }

        var owner = request.LoginOwner.Trim();
        if (!Add(steps, !owner.StartsWith("user:", StringComparison.Ordinal) || owner.Length <= 5
                ? Fail(ClaudeCodeSetupStep.LoginOwner, "Name the one person this login belongs to, for example user:owner.")
                : Ok(ClaudeCodeSetupStep.LoginOwner, "The login belongs to " + owner + ".")))
        {
            return Stop(steps);
        }

        steps.Add(Ok(ClaudeCodeSetupStep.Settings, "Ready to apply."));
        var settings = new ClaudeCodeOptions
        {
            Enabled = true,
            AuthMode = ClaudeCodeAuthMode.Login,
            BinaryPath = binary!,
            ConfigDir = request.ConfigDir,
            LoginOwner = owner,
            TermsAcknowledgedAt = acknowledgedAt,
            Models = [.. request.Models],
        };
        return new ClaudeCodeSetupReport(steps, settings);
    }

    private string? ResolveBinary(string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"];
        foreach (var dir in _path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool Add(List<ClaudeCodeStepResult> steps, ClaudeCodeStepResult step)
    {
        steps.Add(step);
        return step.Status == ClaudeCodeStepStatus.Ok;
    }

    private static ClaudeCodeSetupReport Stop(List<ClaudeCodeStepResult> steps)
    {
        foreach (var step in Enum.GetValues<ClaudeCodeSetupStep>().Where(s => steps.All(r => r.Step != s)))
        {
            steps.Add(new ClaudeCodeStepResult(step, ClaudeCodeStepStatus.Skipped, "Not run: an earlier step failed."));
        }

        return new ClaudeCodeSetupReport(steps, null);
    }

    private static ClaudeCodeStepResult Ok(ClaudeCodeSetupStep step, string message) => new(step, ClaudeCodeStepStatus.Ok, message);

    private static ClaudeCodeStepResult Fail(ClaudeCodeSetupStep step, string message) => new(step, ClaudeCodeStepStatus.Failed, message);
}
