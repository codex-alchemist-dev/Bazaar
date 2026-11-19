namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// Operator settings for the TPM-sealed KEK (doc 14, <c>identity.kek_provider = tpm</c>). The shipped PCR policy is signed: PCR 7 plus a
/// policy authorised by an offline-held signing key, so firmware and kernel updates do not brick the unseal. A raw PCR-7-only policy is
/// the documented fallback and must be asked for. A virtual TPM is refused unless the operator opts in (weaker custody).
/// </summary>
public sealed record TpmOptions(string Pcrs, string? PublicKeyPath, bool AllowRawPcrPolicy, bool AllowVirtual, string KeyFile)
{
    public const string PcrsKey = "Bazaar:Identity:Tpm:Pcrs";
    public const string PublicKeyPathKey = "Bazaar:Identity:Tpm:PublicKeyPath";
    public const string AllowRawPcrPolicyKey = "Bazaar:Identity:Tpm:AllowRawPcrPolicy";
    public const string AllowVirtualKey = "Bazaar:Identity:Tpm:AllowVirtual";
    public const string KeyFileKey = "Bazaar:Identity:Tpm:KeyFile";

    public const string DefaultPcrs = "7";

    /// <summary>The sealed KEK file for a data root.</summary>
    public static string DefaultKeyFile(string dataRoot) => Path.Combine(dataRoot, "keys", "kek-tpm.cred");

    /// <summary>Reads and validates the settings. A bad value throws <see cref="ArgumentException"/>; nothing falls back to another provider.</summary>
    public static TpmOptions From(Func<string, string?> setting, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var pcrs = string.IsNullOrWhiteSpace(setting(PcrsKey)) ? DefaultPcrs : setting(PcrsKey)!.Trim();
        // systemd-creds takes a plus-separated list of PCR numbers (for example "7" or "7+11"); refuse anything that is not.
        if (!pcrs.Split('+').All(p => int.TryParse(p, out var n) && n is >= 0 and <= 23))
            throw new ArgumentException($"{PcrsKey} must be PCR numbers 0 to 23 joined with '+', for example '7'.");

        var publicKey = setting(PublicKeyPathKey);
        publicKey = string.IsNullOrWhiteSpace(publicKey) ? null : publicKey.Trim();
        if (publicKey is not null && !Path.IsPathFullyQualified(publicKey))
            throw new ArgumentException($"{PublicKeyPathKey} must be an absolute path.");

        var allowRaw = Flag(setting, AllowRawPcrPolicyKey);
        if (publicKey is null && !allowRaw)
            throw new ArgumentException(
                $"identity.kek_provider 'tpm' needs {PublicKeyPathKey} (the signed PCR policy, doc 14) or an explicit {AllowRawPcrPolicyKey}=true.");

        var keyFile = setting(KeyFileKey);
        keyFile = string.IsNullOrWhiteSpace(keyFile) ? DefaultKeyFile(dataRoot) : keyFile.Trim();
        if (!Path.IsPathFullyQualified(keyFile))
            throw new ArgumentException($"{KeyFileKey} must be an absolute path.");

        return new TpmOptions(pcrs, publicKey, allowRaw, Flag(setting, AllowVirtualKey), keyFile);
    }

    private static bool Flag(Func<string, string?> setting, string key) =>
        setting(key) is { } v && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
}
