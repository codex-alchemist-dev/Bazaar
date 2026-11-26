using Bazaar.Engines.Roblox.Rojo;

namespace Bazaar.Engines.Tests.Roblox;

/// <summary>Records each invocation and returns a scripted result, so Rojo tests need no real binary.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public sealed record Call(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout);

    public List<Call> Calls { get; } = [];

    /// <summary>Produces the result for a call. Defaults to a clean exit with no output.</summary>
    public Func<Call, ProcessResult> Respond { get; set; } = _ => new ProcessResult(0, string.Empty, string.Empty, TimedOut: false);

    /// <summary>When set, the runner throws this instead of starting anything.</summary>
    public Exception? ThrowOnStart { get; set; }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var call = new Call(fileName, arguments.ToArray(), workingDirectory, timeout);
        Calls.Add(call);
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnStart is { } exception)
        {
            throw exception;
        }

        return Task.FromResult(Respond(call));
    }
}
