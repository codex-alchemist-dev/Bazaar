using System.Text.RegularExpressions;

namespace Bazaar.Server.Setup;

/// <summary>The reference families a secret-typed key may hold (doc 45 section 1.6; doc 03 for the bootstrap families).</summary>
public enum SecretReferenceFamily
{
    /// <summary>Not a reference: a literal value or an unknown scheme. Always refused.</summary>
    None,

    /// <summary>env:NAME. Bootstrap only.</summary>
    Env,

    /// <summary>file:PATH. Bootstrap only.</summary>
    File,

    /// <summary>dpapi:PATH. Bootstrap only.</summary>
    Dpapi,

    /// <summary>systemd-creds:NAME. Bootstrap only.</summary>
    SystemdCreds,

    /// <summary>secret://bazaar/... resolved by ISecretStore (doc 14).</summary>
    SecretStore,
}

/// <summary>
/// Syntax of secret references. Only the shape is checked here; nothing is resolved, so a setup plan never touches a secret.
/// A reference never carries a value, and no error text repeats the text that was checked.
/// </summary>
public static partial class SecretReferences
{
    private const string _envPrefix = "env:";
    private const string _filePrefix = "file:";
    private const string _dpapiPrefix = "dpapi:";
    private const string _systemdCredsPrefix = "systemd-creds:";
    private const string _secretStorePrefix = "secret://bazaar/";

    /// <summary>The families a bootstrap value may use (the key-encryption key and the database credential, doc 03).</summary>
    public static readonly SecretReferenceFamily[] BootstrapFamilies =
    [
        SecretReferenceFamily.Env,
        SecretReferenceFamily.File,
        SecretReferenceFamily.Dpapi,
        SecretReferenceFamily.SystemdCreds,
    ];

    /// <summary>The family every other secret uses (doc 45 section 1.6).</summary>
    public static readonly SecretReferenceFamily[] SecretStoreFamilies = [SecretReferenceFamily.SecretStore];

    /// <summary>Environment variable names: a letter or underscore, then letters, digits or underscores.</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvNameRegex();

    /// <summary>systemd credential names: letters, digits, dot, underscore and dash.</summary>
    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialNameRegex();

    /// <summary>Each path segment of a secret:// reference: starts with a letter or digit.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretSegmentRegex();

    /// <summary>Returns the family of a reference, or <see cref="SecretReferenceFamily.None"/> when the text is not a reference.</summary>
    public static SecretReferenceFamily Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.StartsWith(_envPrefix, StringComparison.Ordinal)) return SecretReferenceFamily.Env;
        if (text.StartsWith(_filePrefix, StringComparison.Ordinal)) return SecretReferenceFamily.File;
        if (text.StartsWith(_dpapiPrefix, StringComparison.Ordinal)) return SecretReferenceFamily.Dpapi;
        if (text.StartsWith(_systemdCredsPrefix, StringComparison.Ordinal)) return SecretReferenceFamily.SystemdCreds;
        if (text.StartsWith(_secretStorePrefix, StringComparison.Ordinal)) return SecretReferenceFamily.SecretStore;
        return SecretReferenceFamily.None;
    }

    /// <summary>True when the text is a well-formed reference of one of the allowed families.</summary>
    public static bool IsValid(string text, IReadOnlyCollection<SecretReferenceFamily> allowed)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(allowed);
        var family = Classify(text);
        return family != SecretReferenceFamily.None && allowed.Contains(family) && IsWellFormed(family, text);
    }

    /// <summary>The literal prefix of a family, for example <c>dpapi:</c> or <c>secret://bazaar/</c>. Empty for <see cref="SecretReferenceFamily.None"/>.</summary>
    public static string PrefixOf(SecretReferenceFamily family) => family switch
    {
        SecretReferenceFamily.Env => _envPrefix,
        SecretReferenceFamily.File => _filePrefix,
        SecretReferenceFamily.Dpapi => _dpapiPrefix,
        SecretReferenceFamily.SystemdCreds => _systemdCredsPrefix,
        SecretReferenceFamily.SecretStore => _secretStorePrefix,
        _ => string.Empty,
    };

    private static bool IsWellFormed(SecretReferenceFamily family, string text)
    {
        var body = text[PrefixOf(family).Length..];
        return family switch
        {
            SecretReferenceFamily.Env => EnvNameRegex().IsMatch(body),
            SecretReferenceFamily.SystemdCreds => CredentialNameRegex().IsMatch(body),
            SecretReferenceFamily.File or SecretReferenceFamily.Dpapi => IsPath(body),
            SecretReferenceFamily.SecretStore => IsSecretPath(body),
            _ => false,
        };
    }

    private static bool IsPath(string body) =>
        body.Length > 0 && body.All(c => !char.IsControl(c));

    private static bool IsSecretPath(string body)
    {
        var segments = body.Split('/');
        return segments.Length > 0 && segments.All(segment => SecretSegmentRegex().IsMatch(segment));
    }
}
