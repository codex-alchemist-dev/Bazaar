namespace Bazaar.Server.Security.Policy;

/// <summary>How a tool's result taints the session that receives it (doc 15, "Enforcement" and "Reads for modules").</summary>
public enum ToolResultTaint
{
    /// <summary>The result adds no taint of its own. The default.</summary>
    None = 0,

    /// <summary>The result is external content (for example a screen frame) and taints the session hard.</summary>
    Hard = 1,
}
