using Bazaar.Server.Components.Backup.Processes;

namespace Bazaar.Server.Components.Backup.Tests;

/// <summary>A temp directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bazaar-backup-" + Guid.CreateVersion7().ToString("N"));

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

/// <summary>Records calls and returns scripted results.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<string, IReadOnlyList<string>, ProcessResult> _respond;

    public FakeProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult>? respond = null) =>
        _respond = respond ?? ((_, _) => new ProcessResult(0, string.Empty, string.Empty));

    public List<(string File, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env)> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        Calls.Add((fileName, arguments.ToList(), environment));
        return Task.FromResult(_respond(fileName, arguments));
    }
}
