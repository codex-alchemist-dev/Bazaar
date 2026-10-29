using Bazaar.Server.Gateway.Policy;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// Operator settings for FreeLLMAPI as the first-class model source (docs/design/05-model-gateway.md). Off by default.
/// <c>ApiKey</c> is a bootstrap secret reference (<c>env:NAME</c> or <c>file:PATH</c>), never the key itself.
/// </summary>
public sealed class FreeLlmApiOptions
{
    public const string SectionName = "Bazaar:Gateway:FreeLlmApi";

    /// <summary>When false the provider, route and key are not registered and no secret is read.</summary>
    public bool Enabled { get; set; }

    /// <summary>OpenAI-compatible base URL, for example <c>http://127.0.0.1:3001/v1</c>. Required when enabled.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Secret reference for the unified <c>freellmapi-</c> key: <c>env:NAME</c> or <c>file:PATH</c>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The upstream model id sent with each call. Must be a concrete id (see <see cref="FreeLlmApiOptionsValidator"/>).</summary>
    public string DefaultModel { get; set; } = "auto";

    /// <summary>The route that points at FreeLLMAPI. The responder's default route is <c>agent-default</c>.</summary>
    public string RouteName { get; set; } = "agent-default";

    /// <summary>Overall time limit for one upstream call, headers and body.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>Checks the FreeLLMAPI options at start. Each failure names its configuration key and never echoes a value.</summary>
public sealed class FreeLlmApiOptionsValidator : IValidateOptions<FreeLlmApiOptions>
{
    private const string _envPrefix = "env:";
    private const string _filePrefix = "file:";

    public ValidateOptionsResult Validate(string? name, FreeLlmApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var section = FreeLlmApiOptions.SectionName;
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add($"{section}:BaseUrl is required when {section}:Enabled is true.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            failures.Add($"{section}:BaseUrl must be an absolute http or https URL.");
        }
        else if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            failures.Add($"{section}:BaseUrl must not carry credentials; put the key in {section}:ApiKey.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add($"{section}:ApiKey is required; use a secret reference such as env:NAME or file:PATH.");
        }
        else if (!IsSecretReference(options.ApiKey))
        {
            failures.Add($"{section}:ApiKey must be a secret reference (env:NAME or file:PATH); a literal key is refused and not shown.");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultModel))
        {
            failures.Add($"{section}:DefaultModel is required.");
        }
        else if (!ModelRef.TryParse($"{FreeLlmApiProvider.ProviderId}:{options.DefaultModel}", out _))
        {
            // ModelRef refuses the global auto family, so a bare "auto" cannot be named in an agent policy. Fail at start, not per call.
            failures.Add($"{section}:DefaultModel '{options.DefaultModel}' is not a usable model id: Bazaar policy refuses the auto family. Set a concrete FreeLLMAPI model id.");
        }

        if (!RouteNames.IsValid(options.RouteName))
        {
            failures.Add($"{section}:RouteName '{options.RouteName}' is not a valid route name.");
        }

        if (options.TimeoutSeconds is < 1 or > 3600)
        {
            failures.Add($"{section}:TimeoutSeconds must be between 1 and 3600.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsSecretReference(string text) =>
        (text.StartsWith(_envPrefix, StringComparison.Ordinal) && text.Length > _envPrefix.Length)
        || (text.StartsWith(_filePrefix, StringComparison.Ordinal) && text.Length > _filePrefix.Length);
}
