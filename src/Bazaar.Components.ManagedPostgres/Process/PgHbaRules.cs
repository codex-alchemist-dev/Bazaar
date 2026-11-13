namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// Rewrites the pg_hba.conf that initdb writes. The bootstrap runs initdb with trust rules, sets the superuser password over
/// that trust, then calls <see cref="SwitchTrustToScram"/> so no trust rule survives into the cluster that serves clients.
/// </summary>
public static class PgHbaRules
{
    public const string Trust = "trust";
    public const string Scram = "scram-sha-256";

    /// <summary>Returns the file with every active trust method replaced by scram-sha-256. Comments and other lines are kept.</summary>
    public static string SwitchTrustToScram(string hba)
    {
        var lines = hba.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsTrustRule(lines[i]))
            {
                var line = lines[i];
                var eol = line.EndsWith('\r') ? "\r" : string.Empty;
                var content = line.TrimEnd('\r').TrimEnd();
                lines[i] = content[..^Trust.Length] + Scram + eol;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>True when any active (non-comment) rule still uses the trust method.</summary>
    public static bool HasTrustRule(string hba) => hba.Split('\n').Any(IsTrustRule);

    private static bool IsTrustRule(string line)
    {
        var content = line.TrimEnd('\r').TrimEnd();
        if (content.Length == 0 || content.TrimStart().StartsWith('#'))
        {
            return false;
        }

        if (!content.EndsWith(Trust, StringComparison.Ordinal))
        {
            return false;
        }

        // The method must be a whole token, not the tail of a longer word.
        return content.Length == Trust.Length || char.IsWhiteSpace(content[^(Trust.Length + 1)]);
    }
}
