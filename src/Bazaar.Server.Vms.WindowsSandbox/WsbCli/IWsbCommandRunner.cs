namespace Bazaar.Server.Vms.WindowsSandbox.WsbCli;

/// <summary>Runs the <c>wsb.exe</c> command-line tool. The only path from the adapter to a process, so tests replace it.</summary>
public interface IWsbCommandRunner
{
    /// <summary>Runs <c>wsb</c> with the given arguments. Arguments are passed as a list, never through a shell.</summary>
    Task<WsbCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>The exit code and captured output of one <c>wsb</c> call.</summary>
public sealed record WsbCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>A <c>wsb</c> call failed, or its output could not be read.</summary>
public sealed class WindowsSandboxCliException : Exception
{
    public WindowsSandboxCliException(string message)
        : base(message)
    {
    }

    public WindowsSandboxCliException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
