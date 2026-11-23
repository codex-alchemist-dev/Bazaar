namespace Bazaar.Client.Athanor.Gtk.Helper;

/// <summary>The helper's process exit codes (doc 57 section 2.1). The proxy logs them; any non-zero code is a crash to it.</summary>
public static class HelperExitCodes
{
    public const int Ok = 0;
    public const int BadArguments = 2;
    public const int SandboxDisabled = 3;
    public const int SandboxUnavailable = 4;
    public const int NoDisplay = 5;
    public const int NativeLibraryMissing = 6;
    public const int BundleMissing = 7;
}

/// <summary>The outcome of the sandbox check.</summary>
public readonly record struct SandboxDecision(bool CanStart, bool SandboxActive, int ExitCode, string? Reason);

/// <summary>
/// The production rule (doc 57 section 2.1): the WebKit sandbox must be on. The helper refuses to start when the
/// sandbox is switched off in the environment or the sandbox tool (bubblewrap) is missing, unless the operator asked
/// for <c>--allow-no-sandbox</c>, in which case the helper runs without it and says so in <c>helper.hello</c>.
/// </summary>
public static class SandboxPolicy
{
    /// <summary>The WebKitGTK environment switch that turns the sandbox off. It is named so on purpose.</summary>
    public const string DisableVariable = "WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS";

    /// <summary>The WebKitGTK environment switch that forces the sandbox on.</summary>
    public const string ForceVariable = "WEBKIT_FORCE_SANDBOX";

    /// <summary>Decides from the options, the disable switch and whether <c>bwrap</c> was found.</summary>
    public static SandboxDecision Evaluate(bool allowNoSandbox, bool disableSwitchSet, bool bubblewrapFound)
    {
        if (allowNoSandbox)
        {
            return new SandboxDecision(true, SandboxActive: !disableSwitchSet && bubblewrapFound, HelperExitCodes.Ok, null);
        }

        if (disableSwitchSet)
        {
            return new SandboxDecision(false, false, HelperExitCodes.SandboxDisabled, $"{DisableVariable} is set. Start with --allow-no-sandbox to run without the sandbox.");
        }

        if (!bubblewrapFound)
        {
            return new SandboxDecision(false, false, HelperExitCodes.SandboxUnavailable, "bubblewrap (bwrap) was not found on PATH, so the WebKit sandbox cannot run.");
        }

        return new SandboxDecision(true, true, HelperExitCodes.Ok, null);
    }

    /// <summary>True when an executable named <c>bwrap</c> is in a <c>PATH</c> directory.</summary>
    public static bool FindBubblewrap(string? pathVariable)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return false;
        }

        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, "bwrap")))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return false;
    }
}
