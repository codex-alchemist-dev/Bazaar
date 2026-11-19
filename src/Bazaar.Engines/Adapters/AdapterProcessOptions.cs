namespace Bazaar.Engines.Adapters;

/// <summary>
/// How to start one adapter child process. <see cref="CommandTimeout"/> applies to each <c>command</c> request
/// (see <see cref="EngineCommandTimeout"/> for the setting's range). <see cref="OutputTailLines"/> sets how many recent
/// output lines are kept for failure reports.
/// </summary>
public sealed record AdapterProcessOptions(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan CommandTimeout,
    string? WorkingDirectory = null,
    int OutputTailLines = 20)
{
    /// <summary>The longest single stdout message, in chars. A longer line fails the session. Default 8 MiB.</summary>
    public int MaxMessageChars { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Environment variables the server sets on purpose. The child otherwise gets only an allowlisted subset of the
    /// server's environment (see <c>ChildProcessEnvironment</c>), never the whole of it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>The lifecycle state of an <see cref="AdapterHost"/>.</summary>
public enum AdapterHostState
{
    /// <summary>The child is running and accepts requests.</summary>
    Running,

    /// <summary>The session failed (timeout, crash or protocol error). The child has been killed.</summary>
    Failed,

    /// <summary>The session was closed normally or disposed.</summary>
    Closed,
}

/// <summary>One output line the adapter streamed as an <c>output</c> notification.</summary>
public sealed record AdapterOutput(string ProjectId, string CommandId, long Seq, string Text);
