using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// Serves the FreeLLMAPI key for the FreeLLMAPI provider's secret reference only. Every other reference goes to
/// <paramref name="fallback"/>, which keeps failing closed as before. The key is read through <see cref="ISecretResolver"/>
/// on each call, so rotating the value behind the reference takes effect without a restart.
/// </summary>
public sealed class FreeLlmApiSecretSource(string keyReference, ISecretResolver resolver, IProviderSecretSource fallback) : IProviderSecretSource
{
    private readonly string _keyReference = keyReference ?? throw new ArgumentNullException(nameof(keyReference));
    private readonly ISecretResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IProviderSecretSource _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct)
    {
        if (!string.Equals(secretRef, FreeLlmApiProvider.SecretRef, StringComparison.Ordinal))
        {
            return _fallback.ResolveAsync(secretRef, ct);
        }

        return ResolveKeyAsync(ct);
    }

    // A resolution failure throws SecretResolutionException, which names the reference and never the value. The gateway
    // turns any throw into a skipped candidate.
    private async ValueTask<string?> ResolveKeyAsync(CancellationToken ct) => await _resolver.ResolveAsync(_keyReference, ct);
}

/// <summary>
/// Start-time check that the configured FreeLLMAPI key resolves, so a missing variable or file stops the start with a
/// clear error instead of failing every call later. The message names the configuration key and the reference, never the value.
/// </summary>
public static class FreeLlmApiStartupCheck
{
    /// <summary>Does nothing when FreeLLMAPI is disabled, so a disabled config never reads a secret.</summary>
    public static async Task VerifyKeyAsync(FreeLlmApiOptions options, ISecretResolver resolver, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await resolver.ResolveAsync(options.ApiKey, ct);
        }
        catch (SecretResolutionException ex)
        {
            throw new InvalidOperationException(
                $"{FreeLlmApiOptions.SectionName}:ApiKey could not be resolved, so FreeLLMAPI cannot start. {ex.Message}",
                ex);
        }
    }
}
