namespace Bazaar.Server.DevSandbox.Linux.Helper;

/// <summary>Process exit codes of the helper itself. After a successful exec the target's own exit code takes over.</summary>
internal static class HelperExit
{
    internal const int Usage = 64;
    internal const int PolicyInvalid = 65;
    internal const int LandlockUnavailable = 70;
    internal const int LandlockFailed = 71;
    internal const int NoNewPrivsFailed = 72;
    internal const int SeccompFailed = 73;
    internal const int ExecFailed = 74;
    internal const int UnsupportedArch = 75;
    internal const int RlimitFailed = 76;
    internal const int PolicyUnreadable = 77;
    internal const int InitFailed = 78;

    /// <summary>The self-test found the denied syscall was permitted (the filter did not stop it).</summary>
    internal const int SelfTestNotDenied = 3;
}

/// <summary>Parsed command line: <c>--policy &lt;file&gt; [options] -- &lt;target&gt; [args]</c>, or <c>--info</c>.</summary>
internal sealed class HelperArguments
{
    internal string? PolicyPath { get; private set; }

    /// <summary>Delete the policy file once it has been read, before any restriction (so the target never sees it).</summary>
    internal bool DeletePolicyAfterRead { get; private set; }

    /// <summary>Test seam: behave as if the kernel had no Landlock. Can only make the helper fail, never weaken it.</summary>
    internal bool SimulateNoLandlock { get; private set; }

    /// <summary>Print the Landlock ABI and architecture and exit; applies nothing.</summary>
    internal bool Info { get; private set; }

    /// <summary>Instead of exec'ing a target, call this syscall after restriction (only <c>socket</c> is supported).</summary>
    internal string? SelfTest { get; private set; }

    internal string? Target { get; private set; }

    internal List<string> TargetArguments { get; } = [];

    /// <summary>Parses <paramref name="args"/>; returns an error text or null.</summary>
    internal string? Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--policy":
                    if (PolicyPath is not null)
                    {
                        return "--policy given twice";
                    }

                    if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        return "--policy needs a file";
                    }

                    if (!HelperPolicyParser.IsCleanAbsolutePath(args[i]))
                    {
                        return "--policy needs an absolute path without '.' or '..' segments";
                    }

                    PolicyPath = args[i];
                    break;
                case "--delete-policy-after-read":
                    if (DeletePolicyAfterRead)
                    {
                        return "--delete-policy-after-read given twice";
                    }

                    DeletePolicyAfterRead = true;
                    break;
                case "--simulate-landlock-unavailable":
                    if (SimulateNoLandlock)
                    {
                        return "--simulate-landlock-unavailable given twice";
                    }

                    SimulateNoLandlock = true;
                    break;
                case "--info":
                    if (Info)
                    {
                        return "--info given twice";
                    }

                    Info = true;
                    break;
                case "--self-test":
                    if (SelfTest is not null)
                    {
                        return "--self-test given twice";
                    }

                    if (++i >= args.Count || args[i] != "socket")
                    {
                        return "--self-test supports only: socket";
                    }

                    SelfTest = args[i];
                    break;
                case "--":
                    if (i + 1 < args.Count)
                    {
                        Target = args[i + 1];
                        for (var j = i + 2; j < args.Count; j++)
                        {
                            TargetArguments.Add(args[j]);
                        }
                    }

                    i = args.Count;
                    break;
                default:
                    return $"unknown argument '{a}'";
            }
        }

        if (Info)
        {
            return null;
        }

        if (PolicyPath is null)
        {
            return "--policy is required";
        }

        if (SelfTest is null && Target is null)
        {
            return "a target after -- is required";
        }

        if (Target is not null && !HelperPolicyParser.IsCleanAbsolutePath(Target))
        {
            return "the target must be an absolute path without '.' or '..' segments";
        }

        return null;
    }
}
