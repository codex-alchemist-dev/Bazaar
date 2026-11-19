namespace Bazaar.Server.Modules.Health;

/// <summary>
/// The status of one module probe (doc 42 section 11, doc 46 section 10). Maps from doc 46's probe results:
/// Pass to Healthy, Warn to Degraded, Fail to Failing, Unknown to Unknown.
/// Doc 46's installation states <c>recovering</c> and <c>frozen</c> are not modelled here: they describe a running
/// recovery action or incident mode for the whole installation, not the result of one module probe.
/// </summary>
public enum ModuleHealthStatus
{
    /// <summary>The probe passed.</summary>
    Healthy,

    /// <summary>The probe passed with a warning, or a soft dependency is not fully healthy.</summary>
    Degraded,

    /// <summary>The probe failed. A probe that times out or throws is also Failing.</summary>
    Failing,

    /// <summary>The probe could not say. Never counted as healthy (doc 46 section 7).</summary>
    Unknown,
}
