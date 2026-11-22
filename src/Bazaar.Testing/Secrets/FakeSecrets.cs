namespace Bazaar.Testing.Secrets;

/// <summary>
/// Fixed, obviously fake credentials for tests that need a secret-shaped value (scanners, redactors, sanitizers).
/// Use these instead of inventing a token literal. Every value is assembled from short pieces, so no complete
/// secret-shaped string exists in the repository text or in git history and the full-history secret scan has
/// nothing to flag; no allowlist entry is needed. None of these is a credential.
/// </summary>
public static class FakeSecrets
{
    private const string _body = "BazaarFake" + "TestOnly";
    private const string _zeros16 = "0000000000000000";
    private const string _zeros32 = _zeros16 + _zeros16;
    private const string _zeros64 = _zeros32 + _zeros32;

    /// <summary>GitHub classic personal access token shape.</summary>
    public const string GitHubPat = "gh" + "p_" + _body + _zeros16 + "00";

    /// <summary>GitHub fine-grained personal access token shape.</summary>
    public const string GitHubFineGrainedPat = "github" + "_pat_" + _body + _zeros64;

    /// <summary>AWS access key id shape.</summary>
    public const string AwsAccessKeyId = "AK" + "IA" + "FAKETESTKEYAAAAA";

    /// <summary>Anthropic API key shape.</summary>
    public const string AnthropicApiKey = "sk-" + "ant-" + "api03-" + _body + _zeros64 + "00000000000" + "AA";

    /// <summary>Slack bot token (prefix only; the scanners under test key on the prefix).</summary>
    public const string SlackBotToken = "xo" + "xb-" + _body + "0000";

    /// <summary>Google API key shape.</summary>
    public const string GoogleApiKey = "AI" + "za" + _body + _zeros16 + "0";

    /// <summary>High-entropy value for the generic-api-key rule and for scanners that key on variable names.</summary>
    public const string GenericApiKey = "bazaar_fake_test_only_" + "api_key_Q9x7Lm2Zk4Vt8Rw1";

    /// <summary>Every fake value, keyed by property name.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>
    {
        [nameof(GitHubPat)] = GitHubPat,
        [nameof(GitHubFineGrainedPat)] = GitHubFineGrainedPat,
        [nameof(AwsAccessKeyId)] = AwsAccessKeyId,
        [nameof(AnthropicApiKey)] = AnthropicApiKey,
        [nameof(SlackBotToken)] = SlackBotToken,
        [nameof(GoogleApiKey)] = GoogleApiKey,
        [nameof(GenericApiKey)] = GenericApiKey,
    };
}
