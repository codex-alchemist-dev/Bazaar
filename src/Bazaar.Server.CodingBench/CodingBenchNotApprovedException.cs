namespace Bazaar.Server.CodingBench;

/// <summary>
/// Thrown when the benchmark starts without the owner's approval (doc 17 settings: <c>harness.benchmark_max_runs</c>, "yes, before the first run").
/// Nothing has run when it is thrown.
/// </summary>
public sealed class CodingBenchNotApprovedException : InvalidOperationException
{
    public CodingBenchNotApprovedException()
        : base("The coding benchmark needs the owner's approval before the first run (harness.benchmark_max_runs).")
    {
    }
}
