namespace Bazaar.Server.DataVolume.Windows;

/// <summary>One process to run. Arguments are an argument list, never a shell string.</summary>
/// <param name="Tool">A system tool name from the allow-list of <see cref="ProcessCommandRunner"/>, for example <c>diskpart.exe</c>.</param>
/// <param name="Arguments">The argument list.</param>
/// <param name="StandardInput">Text written to the process's stdin and then closed; null for none. Secrets travel here and only here.</param>
internal sealed record CommandSpec(string Tool, IReadOnlyList<string> Arguments, string? StandardInput = null);

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Launches a system tool. The seam that keeps process launching out of every other class and lets tests run on Linux.</summary>
internal interface ICommandRunner
{
    ValueTask<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken);
}
