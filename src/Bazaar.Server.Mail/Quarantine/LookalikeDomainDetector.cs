using System.Globalization;
using System.Text;

namespace Bazaar.Server.Mail.Quarantine;

/// <summary>
/// Pure lookalike-domain check for the sender screen (doc 22 inbound step 4). A sender domain is a lookalike of a
/// protected domain when it is a different domain whose confusable skeleton is the same (homoglyphs, punycode forms,
/// digit-for-letter swaps) or whose name is one edit away. No network and no built-in domain list: the protected set
/// is always supplied by the caller.
/// </summary>
public static class LookalikeDomainDetector
{
    // The edit-distance rule only runs when the protected name's main label is at least this long, so short names do not flood the screen.
    private const int _minLabelLengthForEditRule = 4;

    private static readonly IdnMapping _idn = new();

    // Characters that look like an ASCII letter. Cyrillic and Greek forms, a few Latin variants, and look-alike digits.
    private static readonly Dictionary<char, string> _confusables = new()
    {
        ['а'] = "a",
        ['е'] = "e",
        ['о'] = "o",
        ['р'] = "p",
        ['с'] = "c",
        ['у'] = "y",
        ['х'] = "x",
        ['і'] = "i",
        ['ј'] = "j",
        ['ѕ'] = "s",
        ['ԁ'] = "d",
        ['һ'] = "h",
        ['ԛ'] = "q",
        ['ԝ'] = "w",
        ['ɡ'] = "g",
        ['ı'] = "i",
        ['ł'] = "l",
        ['ɑ'] = "a",
        ['ո'] = "n",
        ['α'] = "a",
        ['ο'] = "o",
        ['ν'] = "v",
        ['ρ'] = "p",
        ['ι'] = "i",
        ['κ'] = "k",
        ['υ'] = "u",
        ['τ'] = "t",
        ['0'] = "o",
        ['1'] = "l",
    };

    /// <summary>True when <paramref name="senderDomain"/> imitates, without equalling, any protected domain.</summary>
    /// <param name="senderDomain">The domain from the From address.</param>
    /// <param name="protectedDomains">Domains to protect. Blank entries are ignored.</param>
    public static bool IsLookalike(string senderDomain, IEnumerable<string> protectedDomains)
    {
        ArgumentNullException.ThrowIfNull(protectedDomains);
        var sender = Normalise(senderDomain);
        if (sender.Length == 0)
        {
            return false;
        }

        var protectedNames = protectedDomains.Select(Normalise).Where(name => name.Length > 0).ToList();

        // The sender is the real domain, or a host under one: never a lookalike.
        if (protectedNames.Any(name => IsSameOrSubdomain(sender, name)))
        {
            return false;
        }

        var senderSkeleton = Skeleton(sender);
        foreach (var name in protectedNames)
        {
            if (senderSkeleton == Skeleton(name))
            {
                return true;
            }

            if (MainLabelLength(name) >= _minLabelLengthForEditRule && WithinOneEdit(sender, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The domain as Unicode, lowercased and trimmed. Punycode labels that do not decode are kept as written.</summary>
    private static string Normalise(string? domain)
    {
        var text = (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var labels = text.Split('.');
        for (var i = 0; i < labels.Length; i++)
        {
            if (!labels[i].StartsWith("xn--", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                labels[i] = _idn.GetUnicode(labels[i]);
            }
            catch (ArgumentException)
            {
                // Malformed punycode stays as written; it will not equal a protected name.
            }
        }

        return string.Join('.', labels).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }

    private static bool IsSameOrSubdomain(string sender, string protectedName) =>
        sender == protectedName || sender.EndsWith("." + protectedName, StringComparison.Ordinal);

    /// <summary>Maps confusable characters to ASCII and folds look-alike letter pairs ("rn" to "m", "vv" to "w").</summary>
    private static string Skeleton(string domain)
    {
        var builder = new StringBuilder(domain.Length);
        foreach (var c in domain)
        {
            builder.Append(_confusables.TryGetValue(c, out var mapped) ? mapped : c.ToString());
        }

        return builder.Replace("rn", "m").Replace("vv", "w").ToString();
    }

    private static int MainLabelLength(string domain)
    {
        var labels = domain.Split('.');
        return labels.Length >= 2 ? labels[^2].Length : labels[0].Length;
    }

    /// <summary>True when the two names differ by one insertion, deletion, substitution or adjacent swap.</summary>
    private static bool WithinOneEdit(string a, string b)
    {
        if (a == b || Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        if (a.Length == b.Length)
        {
            var first = -1;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i])
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                    continue;
                }

                // Second difference: only an adjacent swap is still one edit.
                return i == first + 1 && a[first] == b[i] && a[i] == b[first]
                    && string.CompareOrdinal(a, i + 1, b, i + 1, a.Length - i - 1) == 0;
            }

            return first >= 0;
        }

        var (longer, shorter) = a.Length > b.Length ? (a, b) : (b, a);
        var k = 0;
        while (k < shorter.Length && longer[k] == shorter[k])
        {
            k++;
        }

        return string.CompareOrdinal(longer, k + 1, shorter, k, shorter.Length - k) == 0;
    }
}
