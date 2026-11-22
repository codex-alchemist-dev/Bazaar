namespace Bazaar.Server.Setup.Http;

/// <summary>Names and limits of the provisioning API (doc 45 section 1.9). WORKING NAMES until the owner decides them.</summary>
public static class SetupHttpConstants
{
    /// <summary>The header that carries the one-time bootstrap code before the owner exists. The code is never logged or audited.</summary>
    public const string BootstrapCodeHeader = "X-Bazaar-Bootstrap-Code";

    /// <summary>The principal kind that holds the owner role. Matches the HTTP surface's principal kinds.</summary>
    public const string OwnerKind = "owner";

    /// <summary>The audit kind of one provisioning API call (one row per call, whatever the outcome).</summary>
    public const string AuditKind = "setup.api.call";

    /// <summary>The audit actor of a call the gate refused. No identity is proven for it, so no identity is recorded.</summary>
    public const string RefusedActor = "setup:refused";

    /// <summary>The audit actor of a call admitted by the bootstrap code.</summary>
    public const string BootstrapActor = "setup:bootstrap";

    /// <summary>The largest plan body accepted, in bytes. A larger body answers 413 before it is parsed.</summary>
    public const int MaxPlanBytes = 256 * 1024;

    /// <summary>The longest bootstrap code header accepted. A longer one is refused without a lookup.</summary>
    public const int MaxBootstrapCodeLength = 256;

    /// <summary>The number of characters in a plan hash (SHA-256, lowercase hex).</summary>
    public const int PlanHashLength = 64;
}
