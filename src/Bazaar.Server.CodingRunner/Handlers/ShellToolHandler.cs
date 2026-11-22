using Bazaar.Server.Coding.Workspace;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>Limits for the <c>shell</c> tool (doc 17 settings: <c>harness.command_timeout</c>, capped at doc 16's exec maximum).</summary>
public sealed record ShellToolOptions
{
    /// <summary>The timeout when the call names none. Doc 17 default: 15 minutes.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The longest timeout a call may ask for. Doc 16's exec maximum: 2 hours.</summary>
    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Most characters of each of stdout and stderr returned to the model.</summary>
    public int OutputCap { get; init; } = 65_536;

    /// <summary>Throws when a value is out of range, so a bad setting stops the host instead of loosening a limit.</summary>
    public void Validate()
    {
        if (DefaultTimeout <= TimeSpan.Zero || MaxTimeout < DefaultTimeout || MaxTimeout > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultTimeout), "Timeouts must be positive, default at most max, max at most 2 hours.");
        }

        if (OutputCap is < 1_024 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(OutputCap), "The output cap must be from 1024 to 1000000 characters.");
        }
    }
}

/// <summary>
/// The <c>shell</c> tool (doc 17: argv command, cwd jailed, timeout; capability class Exec). It runs in the agent's guest VM
/// through <see cref="Bazaar.Server.Coding.Workspace.IGuestChannel"/>, never on the server host, with no shell between the
/// argv and the process and an empty environment. Command policy is the permission gate's; this handler adds none.
/// </summary>
public sealed class ShellToolHandler : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "shell";

    /// <summary>Exit code for a failed call whose cause is the guest, not the command.</summary>
    public const int GuestUnavailableExitCode = 1;

    private readonly IGuestWorktreeSource _guests;
    private readonly ShellToolOptions _options;

    /// <summary>Creates the handler. The options are validated here.</summary>
    public ShellToolHandler(IGuestWorktreeSource guests, ShellToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(guests);
        _guests = guests;
        _options = options ?? new ShellToolOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass => ToolCapabilityClass.Exec;

    /// <inheritdoc />
    public bool WritesWorktree => true;

    /// <inheritdoc />
    public async Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        IReadOnlyList<string> argv;
        string cwd;
        TimeSpan timeout;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "argv", "cwd", "timeout_seconds");
            argv = ReadArgv(args);
            cwd = args.OptionalString("cwd", 1_024) ?? ".";
            var seconds = args.OptionalInt("timeout_seconds", 1, (int)_options.MaxTimeout.TotalSeconds, (int)_options.DefaultTimeout.TotalSeconds);
            timeout = TimeSpan.FromSeconds(seconds);
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        // Fail closed on the jail before the guest is touched.
        try
        {
            _ = GuestPath.Resolve("/", cwd);
        }
        catch (ArgumentException)
        {
            return CodingToolResults.Failure("path_escape", "The working directory must stay inside the worktree.", CodingToolResults.MalformedExitCode);
        }

        var target = string.IsNullOrWhiteSpace(toolCall.WorktreeKey)
            ? null
            : await _guests.ResolveAsync(toolCall.AgentId, toolCall.WorktreeKey, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return CodingToolResults.Failure("no_worktree", "The call names no worktree this agent holds.", CodingToolResults.MalformedExitCode);
        }

        GuestCommandResult result;
        try
        {
            result = await GuestCommand.RunAsync(target, argv, cwd, timeout, _options.OutputCap, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return CodingToolResults.Failure("path_escape", "The working directory must stay inside the worktree.", CodingToolResults.MalformedExitCode);
        }

        if (result.Faulted)
        {
            return CodingToolResults.Failure("guest_unavailable", "The guest could not complete the command; its outcome is unknown.", GuestUnavailableExitCode);
        }

        // A timeout or a missing exit code is never a zero.
        var outcome = result.TimedOut
            ? new ExecOutcome(result.ExitCode is null or 0 ? -1 : result.ExitCode, TimedOut: true)
            : new ExecOutcome(result.ExitCode);
        return new CodingToolOutput(outcome, CodingToolResults.Json(w =>
        {
            if (result.ExitCode is { } code)
            {
                w.WriteNumber("exit_code", code);
            }
            else
            {
                w.WriteNull("exit_code");
            }

            w.WriteBoolean("timed_out", result.TimedOut);
            w.WriteString("stdout", result.Stdout);
            w.WriteString("stderr", result.Stderr);
            w.WriteBoolean("output_truncated", result.Truncated);
            w.WriteBoolean("output_is_untrusted", true);
        }));
    }

    private static List<string> ReadArgv(CodingToolArgs args)
    {
        var items = args.OptionalStringArray("argv", 64, 4_096) ?? throw new CodingToolArgumentException("argv", "is required");
        if (items.Count == 0)
        {
            throw new CodingToolArgumentException("argv", "must have at least one item");
        }

        foreach (var item in items)
        {
            if (item.Contains('\0'))
            {
                throw new CodingToolArgumentException("argv", "must not contain NUL characters");
            }
        }

        return [.. items];
    }
}
