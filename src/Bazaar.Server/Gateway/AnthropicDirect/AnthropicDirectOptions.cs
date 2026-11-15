using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Policy;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.AnthropicDirect;

/// <summary>
/// Operator settings for the owner's own Anthropic API key as a model source (docs/design/05-model-gateway.md, "Claude lane").
/// Off by default. <c>ApiKey</c> is a bootstrap secret reference (<c>env:NAME</c> or <c>file:PATH</c>), never the key itself.
/// Only an API key is accepted: a subscription or OAuth token is refused when it is read.
/// </summary>
public sealed class AnthropicDirectOptions
{
    public const string SectionName = "Bazaar:Gateway:AnthropicDirect";

    /// <summary>The model that is served when <see cref="Models"/> is not configured: the Haiku 5.5 model id (Haiku-heavy budget).</summary>
    public const string DefaultModel = "claude-haiku-5-5";

    /// <summary>When false the provider, its routes and the key are not registered and no secret is read.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Anthropic API base URL, without <c>/v1</c>. The adapter adds <c>/v1/messages</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Secret reference for the API key: <c>env:NAME</c> or <c>file:PATH</c>. Required when enabled.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// The model ids this source serves. Each one gets a route with the same name as the model id and that model as its only
    /// member, so a caller asks for <c>claude-haiku-5-5</c> and gets that model. Empty means <see cref="DefaultModel"/> only.
    /// </summary>
    public List<string> Models { get; set; } = [];

    /// <summary>The configured model ids, trimmed and without repeats, or <see cref="DefaultModel"/> when none is configured.</summary>
    public IReadOnlyList<string> EffectiveModels()
    {
        var models = Models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return models.Count == 0 ? [DefaultModel] : models;
    }
}

/// <summary>Checks the Anthropic options at start. Each failure names its configuration key and never echoes a secret value.</summary>
public sealed class AnthropicDirectOptionsValidator : IValidateOptions<AnthropicDirectOptions>
{
    public ValidateOptionsResult Validate(string? name, AnthropicDirectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var section = AnthropicDirectOptions.SectionName;
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
        else if (!FreeLlmApiOptionsValidator.IsSecretReference(options.ApiKey))
        {
            failures.Add($"{section}:ApiKey must be a secret reference (env:NAME or file:PATH); a literal key is refused and not shown.");
        }

        foreach (var model in options.EffectiveModels())
        {
            // The model id is also the route name, so it must pass both rules. Neither accepts the auto family.
            if (!ModelRef.TryParse($"{AnthropicDirectProvider.ProviderId}:{model}", out _) || !RouteNames.IsValid(model))
            {
                failures.Add($"{section}:Models entry '{model}' is not a usable model id and route name.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
