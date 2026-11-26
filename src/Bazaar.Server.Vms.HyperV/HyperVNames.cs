using System.Text.RegularExpressions;

namespace Bazaar.Server.Vms.HyperV;

/// <summary>
/// Names the backend gives to Hyper-V objects. The shapes are fixed, so a name that reaches the host adapter is never free text:
/// the adapter validates with the same patterns and refuses anything else.
/// </summary>
public static partial class HyperVNames
{
    public const string VmPrefix = "bazaar-";

    public const string CheckpointPrefix = "bazaar-cp-";

    public static string Vm(Guid id) => VmPrefix + id.ToString("N");

    public static string Checkpoint(Guid id) => CheckpointPrefix + id.ToString("N");

    public static bool IsVmName(string name) => !string.IsNullOrEmpty(name) && VmPattern().IsMatch(name);

    public static bool IsCheckpointName(string name) => !string.IsNullOrEmpty(name) && CheckpointPattern().IsMatch(name);

    [GeneratedRegex("^bazaar-[0-9a-f]{32}\\z")]
    private static partial Regex VmPattern();

    [GeneratedRegex("^bazaar-cp-[0-9a-f]{32}\\z")]
    private static partial Regex CheckpointPattern();
}
