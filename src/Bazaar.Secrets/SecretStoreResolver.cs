using System.Text;
using Bazaar.Server.Secrets;

namespace Bazaar.Secrets;

/// <summary>
/// Resolves <c>secret://bazaar/&lt;segments&gt;</c> references through <see cref="ISecretStore"/> (doc 14). The path maps to the
/// installation scope, and the secret name is the segments joined with a dot and lowercased. Every failure (malformed reference,
/// missing secret, denied access, store fault) becomes a <see cref="SecretResolutionException"/> with a fixed reason: no value and
/// no inner message ever reaches the caller, and a missing secret is an error, never an empty string.
/// </summary>
public sealed class SecretStoreResolver(ISecretStore store) : ISecretResolver
{
    /// <summary>The literal prefix of every reference this resolver handles.</summary>
    public const string Prefix = "secret://bazaar/";

    /// <summary>The host component name the store sees. Policy must grant it <see cref="ResolvePurpose"/> on the installation scope.</summary>
    public const string ComponentName = "secret-reference-resolver";

    /// <summary>The purpose recorded in the audit log for every read made here.</summary>
    public static SecretPurpose ResolvePurpose { get; } = new("secret-reference.resolve");

    private static readonly HostCaller _caller = new(ComponentName);

    /// <summary>The grant the secret policy must hold for this resolver to read a secret in the installation scope.</summary>
    public static SecretGrant RequiredGrant { get; } = new(ComponentName, SecretScope.Installation.Key, null, ResolvePurpose);

    public bool CanResolve(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public async ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!CanResolve(reference))
        {
            throw new SecretResolutionException(reference, "not a secret://bazaar reference");
        }

        var name = ParseName(reference);
        try
        {
            using var value = await store.GetAsync(_caller, SecretScope.Installation, name, ResolvePurpose, cancellationToken);
            var text = Encoding.UTF8.GetString(value.Bytes);
            if (text.Length == 0)
            {
                throw new SecretResolutionException(reference, "the stored secret is empty");
            }

            return text;
        }
        catch (SecretResolutionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SecretNotFoundException)
        {
            throw new SecretResolutionException(reference, "no such secret");
        }
        catch (SecretAccessDeniedException)
        {
            throw new SecretResolutionException(reference, "access denied");
        }
        catch (Exception)
        {
            throw new SecretResolutionException(reference, "the secret store failed");
        }
    }

    private static SecretName ParseName(string reference)
    {
        var path = reference[Prefix.Length..];
        try
        {
            var segments = path.Split('/');
            if (segments.Any(s => s.Length == 0))
            {
                throw new ArgumentException("empty segment");
            }

            return new SecretName(string.Join('.', segments).ToLowerInvariant());
        }
        catch (ArgumentException)
        {
            throw new SecretResolutionException(reference, "malformed reference");
        }
    }
}
