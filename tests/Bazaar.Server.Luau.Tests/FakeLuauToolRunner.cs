using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau.Tests;

/// <summary>Scripted runner. Records each call and answers from a handler, so checker tests need no real binary.</summary>
internal sealed class FakeLuauToolRunner(Func<string, CancellationToken, Task<LuauProcessResult>> handler) : ILuauToolRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout)> Calls { get; } = [];

    public async Task<LuauProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments, workingDirectory, timeout));
        return await handler(fileName, cancellationToken);
    }

    /// <summary>A runner where every tool exits 0 with no output.</summary>
    public static FakeLuauToolRunner AllClean() =>
        new((_, _) => Task.FromResult(new LuauProcessResult(0, string.Empty, string.Empty, TimedOut: false)));
}
