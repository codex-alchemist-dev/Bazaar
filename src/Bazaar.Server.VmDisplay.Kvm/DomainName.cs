using System.Text.RegularExpressions;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>Validates the hypervisor VM name (<c>DisplayTarget.BackendRef</c>) before it reaches an argv or a socket path.</summary>
internal static partial class DomainName
{
    // \A and \z, not ^ and $, so that a trailing newline does not pass.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Whether <paramref name="name"/> is a safe domain name.</summary>
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);
}
