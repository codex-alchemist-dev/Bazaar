using System.Diagnostics;

namespace Bazaar.Server.Portability.Sandbox;

/// <summary>A program and its arguments, before or after a confinement provider wraps it.</summary>
public sealed record LaunchCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// What the parser child must be held to (doc 44 section 8.3). A provider applies what its OS can and reports the rest
/// in <see cref="ConfinementPlan.NotApplied"/>.
/// </summary>
/// <param name="MemoryBytes">Memory ceiling for the child.</param>
/// <param name="CpuSeconds">CPU-time ceiling for the child.</param>
public sealed record ConfinementLimits(long MemoryBytes, int CpuSeconds)
{
    /// <summary>The child gets no network access.</summary>
    public bool NoNetwork { get; init; } = true;

    /// <summary>The child may not start other processes.</summary>
    public bool NoChildProcesses { get; init; } = true;

    /// <summary>The child may not write files, only its output stream.</summary>
    public bool NoFileWrites { get; init; } = true;
}

/// <summary>A started child's kill switch. Disposing it releases any OS object (a Windows job object).</summary>
public interface IProcessGuard : IDisposable
{
    /// <summary>Kills the child and everything it started. Never throws.</summary>
    void KillTree();
}

/// <summary>
/// The launch command a provider settled on, what it applied, what it could not, and an optional hook that
/// runs right after the process starts (a Windows provider assigns the child to a job object there).
/// </summary>
public sealed record ConfinementPlan(
    LaunchCommand Command,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> NotApplied)
{
    /// <summary>Called once with the started process. Null means the runner uses plain process-tree kill.</summary>
    public Func<Process, IProcessGuard>? AttachAfterStart { get; init; }
}

/// <summary>
/// OS confinement of the parser child, behind a provider interface (doc 03 and doc 44 section 8.3). Linux uses
/// rlimits, namespaces and a process group; Windows uses a job object. No OS-specific API sits outside a provider.
/// </summary>
public interface IParserConfinement
{
    /// <summary>Short provider name, shown in diagnostics.</summary>
    string Name { get; }

    /// <summary>Builds the launch plan for one child. Must not start the process.</summary>
    ConfinementPlan Prepare(ConfinementLimits limits, LaunchCommand command);
}
