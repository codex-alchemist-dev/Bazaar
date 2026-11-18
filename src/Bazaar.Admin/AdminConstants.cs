namespace Bazaar.Admin;

/// <summary>Names used by the admin command. The command name is a WORKING NAME until the owner decides it.</summary>
public static class AdminConstants
{
    /// <summary>The command name. The project's AssemblyName is the same text; a test keeps the two equal.</summary>
    public const string CommandName = "bazaar-admin";

    /// <summary>The word that selects the setup commands (doc 45 section 1.1).</summary>
    public const string SetupCommandWord = "setup";

    /// <summary>The word that selects the Kodo commands (doc 46). Its one command is <see cref="KodoVerifyWord"/>.</summary>
    public const string KodoCommandWord = "kodo";

    /// <summary>The Kodo command that verifies a finished setup (doc 45 section 1.12).</summary>
    public const string KodoVerifyWord = "verify";
}
