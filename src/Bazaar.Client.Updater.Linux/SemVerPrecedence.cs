using System.Globalization;

namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// SemVer 2.0 precedence (https://semver.org section 11): build metadata after <c>+</c> is ignored, a pre-release sorts below
/// its release, numeric identifiers compare as numbers and sort below alphanumeric ones.
/// </summary>
internal static class SemVerPrecedence
{
    /// <summary>True when <paramref name="version"/> is MAJOR.MINOR.PATCH with optional pre-release and build metadata.</summary>
    public static bool TryParse(string? version, out ulong[] core, out string[] pre)
    {
        core = [];
        pre = [];
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        string rest = plus >= 0 ? version[..plus] : version;
        int dash = rest.IndexOf('-', StringComparison.Ordinal);
        string numbers = dash >= 0 ? rest[..dash] : rest;
        string[] parts = numbers.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var values = new ulong[3];
        for (int i = 0; i < 3; i++)
        {
            if (!IsNumeric(parts[i]) || !ulong.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        string[] identifiers = [];
        if (dash >= 0)
        {
            identifiers = rest[(dash + 1)..].Split('.');
            foreach (string id in identifiers)
            {
                if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    return false;
                }
            }
        }

        core = values;
        pre = identifiers;
        return true;
    }

    /// <summary>Negative when <paramref name="left"/> has lower precedence. Both must parse.</summary>
    public static int Compare(string left, string right)
    {
        if (!TryParse(left, out var lc, out var lp) || !TryParse(right, out var rc, out var rp))
        {
            throw new ArgumentException("Both versions must be SemVer.");
        }

        for (int i = 0; i < 3; i++)
        {
            int c = lc[i].CompareTo(rc[i]);
            if (c != 0)
            {
                return c;
            }
        }

        if (lp.Length == 0 || rp.Length == 0)
        {
            return lp.Length == rp.Length ? 0 : lp.Length == 0 ? 1 : -1;
        }

        for (int i = 0; i < Math.Min(lp.Length, rp.Length); i++)
        {
            int c = CompareIdentifier(lp[i], rp[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return lp.Length.CompareTo(rp.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        bool ln = left.All(char.IsAsciiDigit);
        bool rn = right.All(char.IsAsciiDigit);
        if (ln && rn)
        {
            // Compare as numbers without parsing, so an identifier of any length is safe.
            string l = left.TrimStart('0');
            string r = right.TrimStart('0');
            return l.Length != r.Length ? l.Length.CompareTo(r.Length) : string.CompareOrdinal(l, r);
        }

        if (ln != rn)
        {
            return ln ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string part) =>
        part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');
}
