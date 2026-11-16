namespace Bazaar.Server.Security.KillSwitch;

/// <summary>The level at which a kill switch is engaged (doc 15, "Audit log and kill switch"; doc 48 section 11).</summary>
public enum KillSwitchScope
{
    /// <summary>Every agent. Freezes egress and write tools; read tools stay on.</summary>
    Global,

    /// <summary>One agent. Denies every effect for that agent only.</summary>
    Agent,
}
