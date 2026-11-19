using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Gateway.AnthropicDirect;

/// <summary>
/// Thrown when the configured Anthropic credential is not an API key. The message names the configuration key and never the value.
/// </summary>
public sealed class AnthropicCredentialRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// Serves the Anthropic key for the Anthropic provider's secret reference only. Every other reference goes to
/// <paramref name="fallback"/>. The key is read through <see cref="ISecretResolver"/> on each call, so rotating the value behind
/// the reference takes effect without a restart.
/// </summary>
public sealed class AnthropicDirectSecretSource(string keyReference, ISecretResolver resolver, IProviderSecretSource fallback) : IProviderSecretSource
{
    private readonly string _keyReference = keyReference ?? throw new ArgumentNullException(nameof(keyReference));
    private readonly ISecretResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IProviderSecretSource _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct)
    {
        if (!string.Equals(secretRef, AnthropicDirectProvider.SecretRef, StringComparison.Ordinal))
        {
            return _fallback.ResolveAsync(secretRef, ct);
        }

        return ResolveKeyAsync(ct);
    }

    // A resolution failure throws SecretResolutionException, and a refused credential throws AnthropicCredentialRefusedException.
    // Neither names the value. The gateway turns any throw into a skipped candidate.
    private async ValueTask<string?> ResolveKeyAsync(CancellationToken ct)
    {
        var key = await _resolver.ResolveAsync(_keyReference, ct).ConfigureAwait(false);
        AnthropicDirectCredential.RequireApiKey(key);
        return key;
    }
}

/// <summary>
/// The credential rule for this source (CLAUDE.md, "Claude lane": subscription credentials never go through the gateway).
/// </summary>
public static class AnthropicDirectCredential
{
    // Anthropic OAuth access and refresh tokens, which are what a Claude subscription login produces. API keys start "sk-ant-api".
    private static readonly string[] _subscriptionPrefixes = ["sk-ant-oat", "sk-ant-ort"];

    /// <summary>True when the value looks like a subscription (OAuth) token and not an API key.</summary>
    public static bool IsSubscriptionToken(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.AsSpan().Trim();
        foreach (var prefix in _subscriptionPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Throws when the value is empty or a subscription token.</summary>
    /// <exception cref="AnthropicCredentialRefusedException">The value is not usable as an API key.</exception>
    public static void RequireApiKey(string? value)
    {
        var section = AnthropicDirectOptions.SectionName;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AnthropicCredentialRefusedException($"{section}:ApiKey resolved to an empty value.");
        }

        if (IsSubscriptionToken(value))
        {
            throw new AnthropicCredentialRefusedException(
                $"{section}:ApiKey resolved to a subscription (OAuth) token. Only an Anthropic API key is accepted; subscription logins never go through the gateway.");
        }
    }
}

/// <summary>
/// Start-time check that the configured Anthropic key resolves and is an API key, so a missing variable or file, or a
/// subscription token, stops the start with a clear error instead of failing every call later.
/// </summary>
public static class AnthropicDirectStartupCheck
{
    /// <summary>Does nothing when the source is disabled, so a disabled config never reads a secret.</summary>
    public static async Task VerifyKeyAsync(AnthropicDirectOptions options, ISecretResolver resolver, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        if (!options.Enabled)
        {
            return;
        }

        string key;
        try
        {
            key = await resolver.ResolveAsync(options.ApiKey, ct).ConfigureAwait(false);
        }
        catch (SecretResolutionException ex)
        {
            throw new InvalidOperationException(
                $"{AnthropicDirectOptions.SectionName}:ApiKey could not be resolved, so the Anthropic source cannot start. {ex.Message}",
                ex);
        }

        AnthropicDirectCredential.RequireApiKey(key);
    }
}
