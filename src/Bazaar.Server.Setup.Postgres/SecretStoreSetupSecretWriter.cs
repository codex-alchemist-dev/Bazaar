using Bazaar.Secrets;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>
/// <see cref="ISetupSecretWriter"/> over <see cref="ISecretStore"/> (doc 14; doc 45 section 1.6). A <c>secret://bazaar/a/b/c</c>
/// reference is stored in the installation scope under the name <c>a.b.c</c>. The value goes to the store once and is zeroed after.
/// </summary>
/// <remarks>
/// Name rule, so two references never share a name: each path segment must be lowercase letters, digits, underscore or hyphen
/// and start with a letter or digit (no dot, no capitals), and the joined name must fit <see cref="SecretName"/>. A reference
/// outside that rule is refused. Errors never carry the reference text or the value. The mapping is this adapter's choice and
/// is listed as an open question for the owner.
/// </remarks>
public sealed class SecretStoreSetupSecretWriter(ISecretStore store) : ISetupSecretWriter
{
    /// <summary>The component name recorded as the caller in the store's audit event.</summary>
    public const string CallerComponent = "setup";

    private const string _prefix = "secret://bazaar/";

    /// <inheritdoc />
    public bool StoreAvailable => true;

    /// <inheritdoc />
    public async Task WriteAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrEmpty(value);
        var name = NameFor(reference);
        using var secret = SecretValue.FromText(value);
        await store.PutAsync(new HostCaller(CallerComponent), SecretScope.Installation, name, secret, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps a reference to its store name, or throws <see cref="ArgumentException"/> without repeating the reference.</summary>
    public static SecretName NameFor(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.StartsWith(_prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A setup secret reference must start with secret://bazaar/.", nameof(reference));
        }

        var segments = reference[_prefix.Length..].Split('/');
        if (segments.Length == 0 || !segments.All(IsSegment))
        {
            throw new ArgumentException(
                "Each segment of a stored secret reference must be lowercase letters, digits, underscore or hyphen.", nameof(reference));
        }

        return new SecretName(string.Join('.', segments));
    }

    private static bool IsSegment(string segment) =>
        segment.Length > 0
        && (char.IsAsciiLetterLower(segment[0]) || char.IsAsciiDigit(segment[0]))
        && segment.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');
}
