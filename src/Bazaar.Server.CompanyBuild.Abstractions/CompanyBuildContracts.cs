using Cdax.Core;

namespace Bazaar.Server.CompanyBuild.Abstractions;

/// <summary>
/// A company's build request (docs/design/37-distribution-matrix-and-packaging.md section 4a): the company and the modules it
/// selected. Only modules in the composed plan are compiled into that company's server and client builds.
/// </summary>
/// <param name="CompanyId">Stable company id, used in output paths and reports.</param>
/// <param name="SelectedModuleIds">Module ids the company chose. Hard dependencies of these are added automatically.</param>
public sealed record CompanyBuildRequest(string CompanyId, IReadOnlyList<string> SelectedModuleIds);

/// <summary>
/// The composed build: the modules to compile, in dependency order (hard edges only set order, owner 2026-11-16).
/// </summary>
/// <param name="CompanyId">The company this plan is for.</param>
/// <param name="Modules">The modules in start order. Soft dependencies are narrowed to the modules in this plan.</param>
/// <param name="AddedDependencies">Hard dependencies added because a selected module needs them, sorted by id.</param>
public sealed record CompanyBuildPlan(
    string CompanyId,
    IReadOnlyList<ComponentManifest> Modules,
    IReadOnlyList<string> AddedDependencies);

/// <summary>The outcome of one company build run, including every retry, even when a later attempt succeeded.</summary>
/// <param name="Succeeded">True when an attempt completed.</param>
/// <param name="Attempts">Attempts made, including the first.</param>
/// <param name="Retries">Attempts after the first. Reported even when the build later succeeds.</param>
/// <param name="FailureMessages">One message per failed attempt, in order.</param>
/// <param name="IdentityPath">The generated identity source file written for this build.</param>
public sealed record CompanyBuildReport(
    bool Succeeded,
    int Attempts,
    int Retries,
    IReadOnlyList<string> FailureMessages,
    string IdentityPath);

/// <summary>A company build request that cannot be composed (unknown module, or a plan the kernel refuses).</summary>
public sealed class CompanyBuildException(string message) : Exception(message);

/// <summary>One client artifact a company build produces (docs/design/37 section 4a): an output id and the project it is built from.</summary>
/// <param name="Id">Output folder name and artifact id, for example <c>windows-msix</c>. Letters, digits, dot, dash and underscore only.</param>
/// <param name="ProjectPath">The client project the target is published from.</param>
public sealed record CompanyBuildTarget(string Id, string ProjectPath);

/// <summary>The outcome of one target within a company build set.</summary>
/// <param name="TargetId">The target's id.</param>
/// <param name="Report">The runner's report, including every retry.</param>
public sealed record CompanyBuildTargetReport(string TargetId, CompanyBuildReport Report);

/// <summary>The outcome of a full company build: one report per target and whether the set is complete.</summary>
/// <param name="CompanyId">The company the set was built for.</param>
/// <param name="FinishedAt">When the set finished.</param>
/// <param name="Targets">One report per target, in build order.</param>
/// <param name="Complete">True only when every target succeeded. A set that is not complete is never published as complete.</param>
/// <param name="IsSigned">Whether signing was configured. Unsigned sets are labelled in the output folder's README.txt.</param>
/// <param name="OutputDirectory">The company's output folder.</param>
public sealed record CompanyBuildSetReport(
    string CompanyId,
    DateTimeOffset FinishedAt,
    IReadOnlyList<CompanyBuildTargetReport> Targets,
    bool Complete,
    bool IsSigned,
    string OutputDirectory);
