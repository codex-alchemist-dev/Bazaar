namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// What the generated company identity of this build must supply to the updater (doc 37 section 4a): the signing key it
/// pins and whether this is the owner's install. These are build constants, never settings or environment values.
/// </summary>
/// <param name="PinnedKeyFingerprint">Lowercase hex fingerprint of the signing key baked into the build.</param>
/// <param name="PinnedKeyPath">Path of the armored public key file baked into the build.</param>
/// <param name="IsOwnerInstall">True on the owner's install; allows the Latest-Commit channel.</param>
public sealed record UpdateBuildConstants(string PinnedKeyFingerprint, string PinnedKeyPath, bool IsOwnerInstall);
