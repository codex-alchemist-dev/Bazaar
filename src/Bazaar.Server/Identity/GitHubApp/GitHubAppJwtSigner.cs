using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>
/// Builds the RS256 JWT that authenticates as a GitHub App (<c>iss</c> = app id, <c>iat</c> = now minus 60 seconds,
/// <c>exp</c> = now plus 9 minutes). The key is parsed per call and disposed. Errors never include the PEM.
/// </summary>
public sealed class GitHubAppJwtSigner(TimeProvider time)
{
    private static readonly TimeSpan _issuedAtSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(9);

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>Signs a JWT for the App.</summary>
    /// <param name="appId">The numeric GitHub App id.</param>
    /// <param name="privateKeyPem">The App's RSA private key in PEM form.</param>
    /// <returns>The compact serialized JWT.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The app id is not positive.</exception>
    /// <exception cref="GitHubAppException">The PEM cannot be read as an RSA private key.</exception>
    public string CreateJwt(long appId, string privateKeyPem)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        var now = _time.GetUtcNow();
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new JwtHeader("RS256", "JWT"), GitHubAppJson.Default.JwtHeader));
        var claims = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new JwtClaims(
                appId.ToString(CultureInfo.InvariantCulture),
                now.Subtract(_issuedAtSkew).ToUnixTimeSeconds(),
                now.Add(_lifetime).ToUnixTimeSeconds()),
            GitHubAppJson.Default.JwtClaims));

        var signingInput = header + "." + claims;

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // Do not chain the parser message into the text: keep the PEM out of every error.
            throw new GitHubAppException("The GitHub App private key could not be read as an RSA PEM key.");
        }

        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64UrlEncode(signature);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
