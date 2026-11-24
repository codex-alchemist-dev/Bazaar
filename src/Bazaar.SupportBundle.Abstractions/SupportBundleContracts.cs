namespace Bazaar.SupportBundle;

/// <summary>One text file a source contributes to a support bundle. Text is redacted before it is written (doc 37 section 4d).</summary>
/// <param name="Path">Relative path inside the zip, forward slashes, no <c>..</c> and no drive. Anything else is rejected.</param>
/// <param name="Text">The file text, not yet redacted.</param>
public sealed record SupportBundleFile(string Path, string Text);

/// <summary>
/// A module's contribution to the support bundle. A source cannot read another source's output, and a source that
/// throws costs only its own files (doc 37 section 4d).
/// </summary>
public interface ISupportBundleSource
{
    /// <summary>A stable name, used to order sources and to label a failure.</summary>
    string Name { get; }

    /// <summary>Returns the files to include. Called once per bundle.</summary>
    IReadOnlyList<SupportBundleFile> Collect();
}

/// <summary>The outcome of one self-check.</summary>
/// <param name="Id">Stable check id, for example <c>modules.load</c>.</param>
/// <param name="Passed">True when the check passed.</param>
/// <param name="Reason">One short line. Plain text, no paths or user names.</param>
public sealed record SelfCheckResult(string Id, bool Passed, string Reason);

/// <summary>One headless start-up check run by <c>--self-check</c> (doc 37 section 4d).</summary>
public interface ISelfCheck
{
    /// <summary>Stable check id.</summary>
    string Id { get; }

    /// <summary>Runs the check. It must not open a window or a network connection.</summary>
    SelfCheckResult Run();
}

/// <summary>The self-check report, schema <c>bazaar.selfcheck/1</c>.</summary>
/// <param name="Version">The client version string.</param>
/// <param name="Checks">One result per check, sorted by id.</param>
public sealed record SelfCheckReport(string Version, IReadOnlyList<SelfCheckResult> Checks)
{
    /// <summary>The report schema id.</summary>
    public const string Schema = "bazaar.selfcheck/1";

    /// <summary>True when no check failed. A report with no checks does not pass.</summary>
    public bool Passed => Checks.Count > 0 && Checks.All(c => c.Passed);
}
