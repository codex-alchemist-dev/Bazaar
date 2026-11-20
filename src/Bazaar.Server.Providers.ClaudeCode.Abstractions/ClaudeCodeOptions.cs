namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>How the CLI authenticates (docs/design/91-claude-code-cli-provider.md section 11).</summary>
public enum ClaudeCodeAuthMode
{
    /// <summary>The operator's own sign-in held by the unmodified CLI in <see cref="ClaudeCodeOptions.ConfigDir"/>. Bazaar never reads it.</summary>
    Login,

    /// <summary>An Anthropic Console API key from a secret reference; the CLI runs with <c>--bare</c>.</summary>
    ApiKey,
}

/// <summary>
/// Operator settings for the Claude Code CLI provider. Off by default. Nothing here holds a credential: <see cref="ApiKey"/>
/// is a secret reference (<c>env:NAME</c> or <c>file:PATH</c>), and login mode has no credential field at all.
/// </summary>
public sealed class ClaudeCodeOptions
{
    public const string SectionName = "Bazaar:Gateway:ClaudeCode";

    public bool Enabled { get; set; }

    public ClaudeCodeAuthMode AuthMode { get; set; } = ClaudeCodeAuthMode.Login;

    /// <summary>Path of the <c>claude</c> executable. Empty means resolve <c>claude</c> from PATH once at start.</summary>
    public string BinaryPath { get; set; } = "";

    /// <summary>The Claude configuration directory holding the operator's login (login mode). Required when enabled in login mode.</summary>
    public string ConfigDir { get; set; } = "";

    /// <summary>Secret reference for the API key (API key mode only).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The single human whose login this is (<c>user:&lt;id&gt;</c>). Required in login mode (rule T5).</summary>
    public string LoginOwner { get; set; } = "";

    /// <summary>When the operator accepted the terms summary (rules T6 and T10). Required when enabled in login mode.</summary>
    public DateTimeOffset? TermsAcknowledgedAt { get; set; }

    /// <summary>Model ids served. Empty means <see cref="DefaultModel"/>.</summary>
    public List<string> Models { get; set; } = [];

    public const string DefaultModel = "claude-haiku-5-5";

    public int MaxConcurrent { get; set; } = 2;

    public int MaxStepsPerHour { get; set; } = 120;

    public TimeSpan MinSpacing { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan FirstEventTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RateLimitDefaultBackoff { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan RateLimitMaxBackoff { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Largest accepted stdout line, in characters.</summary>
    public int MaxLineChars { get; set; } = 4 * 1024 * 1024;

    /// <summary>Extra environment variable names copied to the child (for example proxy settings). Credentials are never copied.</summary>
    public List<string> EnvPassthrough { get; set; } = [];

    public IReadOnlyList<string> EffectiveModels()
    {
        var models = Models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.Ordinal).ToList();
        return models.Count == 0 ? [DefaultModel] : models;
    }
}
