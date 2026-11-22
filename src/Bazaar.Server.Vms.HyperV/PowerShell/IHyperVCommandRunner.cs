namespace Bazaar.Server.Vms.HyperV.PowerShell;

/// <summary>Runs <c>powershell.exe</c>. The only path from the adapter to a process, so tests replace it.</summary>
public interface IHyperVCommandRunner
{
    /// <summary>Runs PowerShell with the given arguments. Arguments are passed as a list, never through a shell.</summary>
    Task<HyperVCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>The exit code and captured output of one PowerShell call.</summary>
public sealed record HyperVCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>A Hyper-V call failed, or its output could not be read.</summary>
public sealed class HyperVCommandException : Exception
{
    public HyperVCommandException(string message)
        : base(message)
    {
    }

    public HyperVCommandException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
