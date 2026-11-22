namespace Bazaar.Server.Portability.Sandbox;

/// <summary>
/// An <see cref="IPortabilityImporter"/> whose Detect and Parse run in the parser child process. Drop it into
/// <see cref="ImportPipeline"/> in place of the in-process importer; the pipeline needs no change. A sandbox failure
/// surfaces as <see cref="ParserSandboxException"/>, which the pipeline reports as a failed job.
/// </summary>
public sealed class SandboxedImporter : IPortabilityImporter
{
    private readonly ParserSandboxRunner _runner;

    public SandboxedImporter(ParserSandboxRunner runner, string id, string version)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(version);
        _runner = runner;
        Id = id;
        Version = version;
    }

    public string Id { get; }

    public string Version { get; }

    public DetectResult Detect(ImportSource source) => _runner.Detect(Id, source);

    public IReadOnlyList<SourceRecord> Parse(ImportSource source) => _runner.Parse(Id, source);
}
