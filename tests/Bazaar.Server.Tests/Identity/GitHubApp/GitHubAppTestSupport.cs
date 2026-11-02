using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Identity.GitHubApp;

namespace Bazaar.Server.Tests.Identity.GitHubApp;

/// <summary>Shared fixtures for the GitHub App tests: a generated RSA key, a canned HTTP handler and a static lease scope.</summary>
internal static class GitHubAppTestSupport
{
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public static RSA CreateKey() => RSA.Create(2048);

    public static string PemOf(RSA rsa) => rsa.ExportRSAPrivateKeyPem();

    public static IReadOnlyDictionary<string, string> Permissions(params (string Name, string Level)[] items) =>
        items.ToDictionary(item => item.Name, item => item.Level, StringComparer.Ordinal);

    public static string Base64UrlDecodeToString(string segment) => Encoding.UTF8.GetString(Base64UrlDecode(segment));

    public static byte[] Base64UrlDecode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    public sealed class StaticLeaseScope(params string[] repositories) : IGitHubLeaseScope
    {
        public ValueTask<IReadOnlyList<string>> GetAllowedRepositoriesAsync(string agentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>(repositories);
    }

    /// <summary>Returns canned responses and records every request it receives.</summary>
    public sealed class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Url, AuthenticationHeaderValue? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization, body));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Records calls to the token client and returns a fixed token.</summary>
    public sealed class FakeInstallationClient(GitHubInstallationToken result) : IGitHubInstallationTokenClient
    {
        public List<(long InstallationId, string Jwt, GitHubAppTokenRequest Request)> Calls { get; } = [];

        public ValueTask<GitHubInstallationToken> CreateInstallationTokenAsync(
            long installationId,
            string appJwt,
            GitHubAppTokenRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add((installationId, appJwt, request));
            return ValueTask.FromResult(result);
        }
    }
}
