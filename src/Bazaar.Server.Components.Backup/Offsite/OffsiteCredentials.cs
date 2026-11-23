namespace Bazaar.Server.Components.Backup.Offsite;

/// <summary>
/// Resolves <c>backup.offsite_credentials_ref</c> to the text it names. The host maps this to its secret resolution
/// (doc 14); the backup project never reads a secret from plain configuration. Implementations throw when the
/// reference cannot be resolved, with an error that names the reference and never a value.
/// </summary>
public interface IOffsiteCredentialSource
{
    Task<string> ReadAsync(string reference, CancellationToken ct);
}

/// <summary>Fail-closed default: no secret resolution exists, so a configured reference cannot be used.</summary>
public sealed class UnavailableOffsiteCredentialSource : IOffsiteCredentialSource
{
    public Task<string> ReadAsync(string reference, CancellationToken ct) =>
        throw new InvalidOperationException("No secret resolver is available for the offsite credentials reference.");
}

/// <summary>Parses the credentials text into restic environment variables (doc 33 "Offsite composition [PROPOSED]").</summary>
public static class OffsiteCredentialParser
{
    /// <summary>The only names accepted. Anything else is refused so a reference cannot inject arbitrary environment.</summary>
    public static readonly IReadOnlySet<string> AllowedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "RESTIC_PASSWORD",
        "RESTIC_PASSWORD_FILE",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_DEFAULT_REGION",
        "B2_ACCOUNT_ID",
        "B2_ACCOUNT_KEY",
    };

    /// <summary>Reads <c>NAME=value</c> lines. Blank lines and lines starting with <c>#</c> are ignored. Error text never repeats a value.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                throw new FormatException($"Offsite credentials line {lineNumber} is not NAME=value.");
            var name = line[..eq].Trim();
            if (!AllowedNames.Contains(name))
                throw new FormatException($"Offsite credentials line {lineNumber} names an environment variable that is not allowed.");
            result[name] = line[(eq + 1)..];
        }

        return result;
    }
}
