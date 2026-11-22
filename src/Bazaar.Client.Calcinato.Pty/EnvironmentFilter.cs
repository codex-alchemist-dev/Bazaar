using System.Text;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// Builds the environment for a local shell. Names on the deny list never reach the shell, whether they come from the
/// spec or from the inherited environment (doc 52, section 5.3).
/// </summary>
public static class EnvironmentFilter
{
    private static readonly string[] _deniedFragments = ["TOKEN", "SECRET", "KEY", "PASSWORD"];
    private const string _deniedPrefix = "BAZAAR_";

    /// <summary>True when a variable with this name must not reach the shell. The match is case-insensitive.</summary>
    public static bool IsDenied(string name)
    {
        if (name.StartsWith(_deniedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var fragment in _deniedFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Merges the inherited environment with the spec environment. Spec values win over inherited ones. Denied names
    /// are dropped from both.
    /// </summary>
    /// <param name="inherited">The inherited environment, or null for none.</param>
    /// <param name="spec">The spec environment, or null for none.</param>
    /// <param name="comparer">Name comparer: ordinal on Unix, ignore-case on Windows.</param>
    public static Dictionary<string, string> Build(
        IEnumerable<KeyValuePair<string, string>>? inherited,
        IEnumerable<KeyValuePair<string, string>>? spec,
        StringComparer comparer)
    {
        var result = new Dictionary<string, string>(comparer);
        if (inherited is not null)
        {
            Add(result, inherited);
        }

        if (spec is not null)
        {
            Add(result, spec);
        }

        return result;
    }

    private static void Add(Dictionary<string, string> target, IEnumerable<KeyValuePair<string, string>> source)
    {
        foreach (var (name, value) in source)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=') || IsDenied(name))
            {
                continue;
            }

            target[name] = value;
        }
    }

    /// <summary>Builds a UTF-16 environment block for CreateProcessW: NAME=VALUE pairs, each ended by NUL, and a final NUL.</summary>
    public static string WindowsBlock(IEnumerable<KeyValuePair<string, string>> env)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in env)
        {
            builder.Append(name).Append('=').Append(value).Append('\0');
        }

        return builder.Append('\0').ToString();
    }
}
