using System.Text.RegularExpressions;

namespace Bazaar.Server.Vms.Kvm;

/// <summary>
/// Names the backend gives to libvirt objects. The shapes are fixed, so a name that reaches the host adapter is never free text:
/// the adapter validates with the same patterns and refuses anything else.
/// </summary>
public static partial class KvmNames
{
    public const string DomainPrefix = "bazaar-";

    public const string CheckpointPrefix = "bazaar-cp-";

    public static string Domain(Guid id) => DomainPrefix + id.ToString("N");

    public static string Checkpoint(Guid id) => CheckpointPrefix + id.ToString("N");

    public static bool IsDomainName(string name) => !string.IsNullOrEmpty(name) && DomainPattern().IsMatch(name);

    public static bool IsCheckpointName(string name) => !string.IsNullOrEmpty(name) && CheckpointPattern().IsMatch(name);

    [GeneratedRegex("^bazaar-[0-9a-f]{32}$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex("^bazaar-cp-[0-9a-f]{32}$")]
    private static partial Regex CheckpointPattern();
}
