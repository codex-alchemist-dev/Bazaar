using System.Text.RegularExpressions;

namespace Bazaar.Server.Vms.MacOs;

/// <summary>
/// The macOS restore image a golden image is installed from (doc 62 section 5.3): an Apple restore image (IPSW) pinned by id, build
/// and SHA-256, so the parent content is pinned (the doc 16 recipe).
/// <para>Apple rule: macOS runs in a virtual machine only on Apple-branded hardware, with at most two instances per Mac.</para>
/// </summary>
/// <param name="Id">An operator-chosen id for the image, for example <c>macos-15-1</c>.</param>
/// <param name="Build">Apple's build number, for example <c>24B83</c>.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the restore image file.</param>
public sealed record MacOsRestoreImagePin(string Id, string Build, string Sha256)
{
    /// <summary>Throws <see cref="ArgumentException"/> when a part is not in its documented form.</summary>
    public MacOsRestoreImagePin Validated()
    {
        _ = MacOsIdentifiers.RestoreImageId(Id);
        _ = MacOsIdentifiers.Build(Build);
        _ = MacOsIdentifiers.Sha256(Sha256);
        return this;
    }
}

/// <summary>Apple's limit on macOS virtual machines per Mac (doc 62 section 3).</summary>
public static class MacOsInstanceCap
{
    /// <summary>Two virtual instances per Mac. Settings may lower the provider's cap, never raise it.</summary>
    public const int HardCap = 2;

    /// <summary>The cap to use: the setting, never above <see cref="HardCap"/>, never below 1.</summary>
    public static int Effective(int configured) => Math.Clamp(configured, 1, HardCap);
}

/// <summary>Validators for the identifiers that reach the Mac node. A rejected value throws; nothing is repaired.</summary>
public static partial class MacOsIdentifiers
{
    /// <summary>A restore image id: letters, digits, dot, dash and underscore.</summary>
    public static string RestoreImageId(string value) => Check(value, IdRegex(), 64, "restore image id");

    /// <summary>An Apple build number such as <c>24B83</c>.</summary>
    public static string Build(string value) => Check(value, BuildRegex(), 16, "restore image build");

    /// <summary>A lower-case hex SHA-256.</summary>
    public static string Sha256(string value) => Check(value, Sha256Regex(), 64, "restore image SHA-256");

    private static string Check(string value, Regex regex, int max, string what)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > max || !regex.IsMatch(value))
        {
            throw new ArgumentException($"Invalid {what}.", nameof(value));
        }

        return value;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[0-9]{1,2}[A-Z][0-9]{1,5}[a-z]?$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildRegex();

    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
