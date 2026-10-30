using System.Text.RegularExpressions;

namespace Bazaar.Server.Memory.Notes;

/// <summary>
/// Pattern scan for credential-shaped text (docs/design/08-memory-system.md, section 2, step 3). A match
/// rejects the write. Only the shape is matched; the matched text is never returned or logged.
/// </summary>
public static partial class SecretScanner
{
    /// <summary>True when the text contains a secret-shaped token.</summary>
    public static bool ContainsSecret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Patterns().Any(p => p.IsMatch(text));
    }

    private static IEnumerable<Regex> Patterns()
    {
        yield return ApiKeyPrefix();
        yield return GitHubToken();
        yield return GitHubFineGrainedToken();
        yield return AwsAccessKeyId();
        yield return GoogleApiKey();
        yield return SlackToken();
        yield return PemPrivateKey();
        yield return BearerToken();
    }

    /// <summary>OpenAI and Anthropic style keys: <c>sk-</c> or <c>sk-ant-</c> then a long token.</summary>
    [GeneratedRegex(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{20,}")]
    private static partial Regex ApiKeyPrefix();

    /// <summary>Classic GitHub tokens: ghp_, gho_, ghu_, ghs_, ghr_.</summary>
    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{30,}\b")]
    private static partial Regex GitHubToken();

    /// <summary>GitHub fine-grained personal access tokens.</summary>
    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{22,}")]
    private static partial Regex GitHubFineGrainedToken();

    /// <summary>AWS access key ids: AKIA or ASIA plus 16 upper-case letters or digits.</summary>
    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKeyId();

    /// <summary>Google API keys.</summary>
    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b")]
    private static partial Regex GoogleApiKey();

    /// <summary>Slack tokens: xoxb-, xoxa-, xoxp-, xoxo-, xoxr-, xoxs-.</summary>
    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    /// <summary>PEM private key headers, including OpenSSH and PKCS#8 variants.</summary>
    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----")]
    private static partial Regex PemPrivateKey();

    /// <summary>A bearer credential: the word Bearer followed by a long token.</summary>
    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{20,}")]
    private static partial Regex BearerToken();
}
