namespace Bazaar.Server.DevSandbox.Linux.Helper;

/// <summary>
/// Landlock access-right masks per ABI version (kernel <c>Documentation/userspace-api/landlock.rst</c>). The ruleset must handle only
/// rights the running kernel knows, so the masks degrade with the ABI the kernel reports. Pure, so unit tests can check every ABI.
/// </summary>
internal static class LandlockRights
{
    internal const ulong Execute = 1UL << 0;
    internal const ulong WriteFile = 1UL << 1;
    internal const ulong ReadFile = 1UL << 2;
    internal const ulong ReadDir = 1UL << 3;
    internal const ulong RemoveDir = 1UL << 4;
    internal const ulong RemoveFile = 1UL << 5;
    internal const ulong MakeChar = 1UL << 6;
    internal const ulong MakeDir = 1UL << 7;
    internal const ulong MakeReg = 1UL << 8;
    internal const ulong MakeSock = 1UL << 9;
    internal const ulong MakeFifo = 1UL << 10;
    internal const ulong MakeBlock = 1UL << 11;
    internal const ulong MakeSym = 1UL << 12;
    internal const ulong Refer = 1UL << 13;
    internal const ulong Truncate = 1UL << 14;
    internal const ulong IoctlDev = 1UL << 15;

    internal const ulong NetBindTcp = 1UL << 0;
    internal const ulong NetConnectTcp = 1UL << 1;

    internal const ulong ScopeAbstractUnixSocket = 1UL << 0;
    internal const ulong ScopeSignal = 1UL << 1;

    /// <summary>Rights that only make sense on a file (a rule on a regular file with any other right is rejected by the kernel).</summary>
    internal const ulong FileOnlyAllowed = Execute | WriteFile | ReadFile | Truncate | IoctlDev;

    /// <summary>Every file system right the given ABI version knows.</summary>
    internal static ulong HandledFs(int abi)
    {
        ulong rights = 0;
        if (abi >= 1)
        {
            rights |= Execute | WriteFile | ReadFile | ReadDir | RemoveDir | RemoveFile | MakeChar | MakeDir | MakeReg | MakeSock | MakeFifo | MakeBlock | MakeSym;
        }

        if (abi >= 2)
        {
            rights |= Refer;
        }

        if (abi >= 3)
        {
            rights |= Truncate;
        }

        if (abi >= 5)
        {
            rights |= IoctlDev;
        }

        return rights;
    }

    /// <summary>The TCP rights handled from ABI 4 (with no rules, all TCP bind and connect are denied).</summary>
    internal static ulong HandledNet(int abi) => abi >= 4 ? NetBindTcp | NetConnectTcp : 0;

    /// <summary>The scopes from ABI 6: abstract unix sockets and signals to processes outside the domain.</summary>
    internal static ulong Scopes(int abi) => abi >= 6 ? ScopeAbstractUnixSocket | ScopeSignal : 0;

    /// <summary>Size of <c>struct landlock_ruleset_attr</c> to pass for the ABI (the kernel accepts the older, shorter sizes).</summary>
    internal static uint RulesetAttrSize(int abi) => abi >= 6 ? 24u : abi >= 4 ? 16u : 8u;

    /// <summary>Read and execute, limited to what the ABI handles.</summary>
    internal static ulong ReadExec(int abi) => (Execute | ReadFile | ReadDir) & HandledFs(abi);

    /// <summary>Read and write (no execute), limited to what the ABI handles. Includes create, remove, rename, truncate.</summary>
    internal static ulong ReadWrite(int abi) => HandledFs(abi) & ~Execute;

    /// <summary>Restricts <paramref name="rights"/> to what the ABI handles, and for a non-directory to the file-only rights.</summary>
    internal static ulong ForPath(ulong rights, int abi, bool isDirectory)
    {
        var masked = rights & HandledFs(abi);
        return isDirectory ? masked : masked & FileOnlyAllowed;
    }
}
