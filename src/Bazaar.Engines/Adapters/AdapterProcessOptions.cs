namespace Bazaar.Engines.Adapters;

/// <summary>
/// How to start one adapter child process. <see cref="CommandTimeout"/> applies to each <c>command</c> request
/// (see <see cref="EngineCommandTimeout"/> for the setting's range). <see cref="OutputTailLines"/> sets how many recent
/// output lines are kept for failure reports. <see cref="TimeProvider"/> drives the command timeout clock; null means the
/// system clock, and tests pass a fake one so a timeout fires exactly when the test says.
/// </summary>
public sealed record AdapterProcessOptions(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan CommandTimeout,
    string? WorkingDirectory = null,
    int OutputTailLines = 20,
    TimeProvider? TimeProvider = null);

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
