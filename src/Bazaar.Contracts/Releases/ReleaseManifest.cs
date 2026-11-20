namespace Bazaar.Contracts.Releases;

/// <summary>
/// A release as the update feed publishes it (doc 30 sections 4, 5 and 7): version, channel, the protocol range the
/// build speaks, the schema it needs, the key that signed it and one <see cref="ReleaseArtifact"/> per build.
/// Immutable and validated on construction (<see cref="ArgumentException"/>), including when decoded from JSON.
/// The Windows Velopack path (<c>Bazaar.Updates.UpdateManifest</c>) is separate and unchanged.
/// </summary>
public sealed record ReleaseManifest : IEquatable<ReleaseManifest>
{
    /// <summary>Upper bound for the number of artifacts in one manifest.</summary>
    public const int MaxArtifacts = 64;

    /// <summary>Creates a validated manifest.</summary>
    /// <param name="channel">One of <see cref="ReleaseChannels.All"/>; the version's pre-release suffix must match it.</param>
    /// <param name="appVersion">SemVer string such as <c>0.1.0</c>, <c>0.2.0-beta.1</c> or <c>0.1.0+a1b2c3d</c> (no leading <c>v</c>).</param>
    /// <param name="minProtocol">Lowest wire protocol the build speaks, <c>major.minor</c> (doc 02).</param>
    /// <param name="maxProtocol">Highest wire protocol the build speaks, <c>major.minor</c>; not below <paramref name="minProtocol"/>.</param>
    /// <param name="schemaVersion">Database schema the build needs (doc 04), 1 to 32 characters of letters, digits, dot, underscore or hyphen.</param>
    /// <param name="signingKeyFingerprint">Lowercase hex (40 or 64 characters) identity of the signing key: the OpenPGP fingerprint for Linux, the pinned certificate's thumbprint for Windows.</param>
    /// <param name="artifacts">At least one, at most <see cref="MaxArtifacts"/>; all for one os; no two share architecture and format.</param>
    /// <exception cref="ArgumentException">A field is missing or malformed.</exception>
    public ReleaseManifest(
        string channel, string appVersion, string minProtocol, string maxProtocol,
        string schemaVersion, string signingKeyFingerprint, IReadOnlyList<ReleaseArtifact> artifacts)
    {
        Channel = ReleaseValidation.Known(channel, ReleaseChannels.All, nameof(channel));
        AppVersion = ReleaseValidation.SemVer(appVersion, nameof(appVersion));
        ReleaseValidation.ChannelMatchesVersion(Channel, AppVersion, nameof(appVersion));

        var min = ReleaseValidation.Protocol(minProtocol, nameof(minProtocol));
        var max = ReleaseValidation.Protocol(maxProtocol, nameof(maxProtocol));
        if (min.CompareTo(max) > 0)
        {
            throw new ArgumentException("minProtocol is above maxProtocol.", nameof(minProtocol));
        }

        MinProtocol = minProtocol;
        MaxProtocol = maxProtocol;
        SchemaVersion = ReleaseValidation.SchemaVersion(schemaVersion, nameof(schemaVersion));

        if (signingKeyFingerprint is null || signingKeyFingerprint.Length is not (40 or 64))
        {
            throw new ArgumentException("Fingerprint must be 40 or 64 lowercase hex characters.", nameof(signingKeyFingerprint));
        }

        SigningKeyFingerprint = ReleaseValidation.LowerHex(signingKeyFingerprint, signingKeyFingerprint.Length, nameof(signingKeyFingerprint));

        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count is < 1 or > MaxArtifacts)
        {
            throw new ArgumentException($"A manifest needs 1 to {MaxArtifacts} artifacts.", nameof(artifacts));
        }

        var copy = new ReleaseArtifact[artifacts.Count];
        var seen = new HashSet<(string, string, string)>();
        for (int i = 0; i < copy.Length; i++)
        {
            var artifact = artifacts[i] ?? throw new ArgumentException("An artifact is null.", nameof(artifacts));
            if (artifact.Os != artifacts[0].Os)
            {
                // One signing key (and one signing scheme) covers a manifest, so a feed serves one manifest per OS.
                throw new ArgumentException("All artifacts in a manifest must share one os.", nameof(artifacts));
            }

            if (!seen.Add((artifact.Os, artifact.Architecture, artifact.Format)))
            {
                throw new ArgumentException(
                    $"Duplicate artifact for {artifact.Architecture} / {artifact.Format}.", nameof(artifacts));
            }

            copy[i] = artifact;
        }

        Artifacts = Array.AsReadOnly(copy);
    }

    /// <summary>Release channel, an exact <see cref="ReleaseChannels"/> name.</summary>
    public string Channel { get; }

    /// <summary>SemVer version of this release.</summary>
    public string AppVersion { get; }

    /// <summary>Lowest wire protocol the build speaks (<c>major.minor</c>).</summary>
    public string MinProtocol { get; }

    /// <summary>Highest wire protocol the build speaks (<c>major.minor</c>).</summary>
    public string MaxProtocol { get; }

    /// <summary>Database schema version the build needs.</summary>
    public string SchemaVersion { get; }

    /// <summary>Lowercase hex fingerprint of the OpenPGP signing key.</summary>
    public string SigningKeyFingerprint { get; }

    /// <summary>The builds on offer. Never empty.</summary>
    public IReadOnlyList<ReleaseArtifact> Artifacts { get; }

    /// <summary>Finds the artifact for an architecture and format, or null when the release has none.</summary>
    public ReleaseArtifact? Find(string architecture, string format)
    {
        foreach (var artifact in Artifacts)
        {
            if (artifact.Architecture == architecture && artifact.Format == format) return artifact;
        }

        return null;
    }

    /// <summary>Value equality including the artifact list (the default record comparison would compare the list by reference).</summary>
    public bool Equals(ReleaseManifest? other) =>
        other is not null
        && Channel == other.Channel && AppVersion == other.AppVersion
        && MinProtocol == other.MinProtocol && MaxProtocol == other.MaxProtocol
        && SchemaVersion == other.SchemaVersion && SigningKeyFingerprint == other.SigningKeyFingerprint
        && Artifacts.SequenceEqual(other.Artifacts);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Channel);
        hash.Add(AppVersion);
        hash.Add(SigningKeyFingerprint);
        foreach (var artifact in Artifacts) hash.Add(artifact);
        return hash.ToHashCode();
    }
}
