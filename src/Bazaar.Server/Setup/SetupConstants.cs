namespace Bazaar.Server.Setup;

/// <summary>Names and versions fixed by doc 45 (onboarding). Every name here is a WORKING NAME until the owner decides it.</summary>
public static class SetupConstants
{
    /// <summary>The declarative config file (doc 45 section 1.5). WORKING NAME, pending the owner's naming decision.</summary>
    public const string ConfigFileName = "bazaar-setup.json";

    /// <summary>The only <c>schemaVersion</c> this build reads (doc 45 section 1.5).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The actor id recorded when the setup plan validates settings. The plan is applied by an operator, so the role is Operator.</summary>
    public const string PlanActorId = "setup:plan";
}
