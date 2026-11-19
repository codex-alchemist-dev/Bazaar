namespace Bazaar.Updates;

/// <summary>
/// One release offered by a company server's update feed. The feed is untrusted until the package
/// hash and signature both check out (see <see cref="UpdateVerifier"/>).
/// </summary>
/// <param name="Version">The SemVer string of the release, as the company server built it.</param>
/// <param name="PackageUrl">Where to download the package. Must be HTTPS, or HTTP on loopback.</param>
/// <param name="Sha256Hex">Hex SHA-256 of the package bytes.</param>
/// <param name="Signature">Signature over the package bytes, made with the company's signing certificate.</param>
public sealed record UpdateManifest(string Version, Uri PackageUrl, string Sha256Hex, byte[] Signature);
