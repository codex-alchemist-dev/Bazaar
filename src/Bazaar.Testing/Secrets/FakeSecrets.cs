namespace Bazaar.Testing.Secrets;

/// <summary>
/// Fixed, obviously fake credentials for tests that need a secret-shaped value (scanners, redactors, sanitizers).
/// Use these instead of inventing a token literal: <c>.gitleaks.toml</c> allowlists exactly these values (anchored
/// regexes, no path or rule skips), so any other secret-shaped literal in a commit turns the full-history secret
/// scan red for every PR. Do not change a value without updating the allowlist; <c>FakeSecretsAllowlistTests</c>
/// fails if they drift. None of these is a credential.
/// </summary>
public static class FakeSecrets
{
    /// <summary>GitHub classic personal access token (`ghp_`).</summary>
    public const string GitHubPat = "ghp_BazaarFakeTestOnly000000000000000000";

    /// <summary>GitHub fine-grained personal access token (`github_pat_`).</summary>
    public const string GitHubFineGrainedPat = "github_pat_BazaarFakeTestOnly0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>AWS access key id (`AKIA`).</summary>
    public const string AwsAccessKeyId = "AKIAFAKETESTKEYAAAAA";

    /// <summary>Anthropic API key (`sk-ant-api03-`).</summary>
    public const string AnthropicApiKey = "sk-ant-api03-BazaarFakeTestOnly000000000000000000000000000000000000000000000000000000000000000000000000000AA";

    /// <summary>Slack bot token (`xoxb-`).</summary>
    public const string SlackBotToken = "xoxb-BazaarFakeTestOnly0000";

    /// <summary>Google API key (`AIza`).</summary>
    public const string GoogleApiKey = "AIzaBazaarFakeTestOnly00000000000000000";

    /// <summary>High-entropy value for the generic-api-key rule and for scanners that key on variable names.</summary>
    public const string GenericApiKey = "bazaar_fake_test_only_api_key_Q9x7Lm2Zk4Vt8Rw1";

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
