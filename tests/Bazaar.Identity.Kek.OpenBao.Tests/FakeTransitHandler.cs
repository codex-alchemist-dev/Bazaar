using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Bazaar.Identity.Kek.OpenBao.Tests;

/// <summary>
/// An in-memory stand-in for OpenBao's transit engine, speaking the same HTTP shapes. A derived key binds the request <c>context</c>
/// into the AES-GCM associated data, so a wrap opens only for the context it was made with, as the real engine does.
/// </summary>
public sealed class FakeTransitHandler(string token = "s.test-token") : HttpMessageHandler
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly List<(string Method, string Path, string? Namespace)> _requests = [];

    public bool KeyExists { get; set; }
    public bool KeyIsDerived { get; set; } = true;
    public HttpStatusCode? FailWith { get; set; }
    public IReadOnlyList<(string Method, string Path, string? Namespace)> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _requests.Add((request.Method.Method, path, request.Headers.TryGetValues("X-Vault-Namespace", out var ns) ? ns.Single() : null));

        if (FailWith is { } status)
            return new HttpResponseMessage(status);
        if (!request.Headers.TryGetValues("X-Vault-Token", out var tokens) || tokens.Single() != token)
            return new HttpResponseMessage(HttpStatusCode.Forbidden);

        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // v1, mount, op, name
        var op = parts[2];

        switch (op)
        {
            case "keys" when request.Method == HttpMethod.Get:
                return KeyExists
                    ? Json(new JsonObject { ["data"] = new JsonObject { ["derived"] = KeyIsDerived, ["type"] = "aes256-gcm96" } })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            case "keys":
                KeyExists = true;
                KeyIsDerived = body!["derived"]!.GetValue<bool>();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "encrypt":
            {
                var nonce = RandomNumberGenerator.GetBytes(12);
                var plain = Convert.FromBase64String(body!["plaintext"]!.GetValue<string>());
                var context = Convert.FromBase64String(body["context"]!.GetValue<string>());
                var cipher = new byte[plain.Length];
                var tag = new byte[16];
                using (var gcm = new AesGcm(_key, 16))
                    gcm.Encrypt(nonce, plain, cipher, tag, context);
                var packed = nonce.Concat(cipher).Concat(tag).ToArray();
                return Json(new JsonObject { ["data"] = new JsonObject { ["ciphertext"] = "vault:v1:" + Convert.ToBase64String(packed) } });
            }

            case "decrypt":
            {
                var text = body!["ciphertext"]!.GetValue<string>();
                var context = Convert.FromBase64String(body["context"]!.GetValue<string>());
                try
                {
                    var packed = Convert.FromBase64String(text["vault:v1:".Length..]);
                    var nonce = packed[..12];
                    var tag = packed[^16..];
                    var cipher = packed[12..^16];
                    var plain = new byte[cipher.Length];
                    using (var gcm = new AesGcm(_key, 16))
                        gcm.Decrypt(nonce, cipher, tag, plain, context);
                    return Json(new JsonObject { ["data"] = new JsonObject { ["plaintext"] = Convert.ToBase64String(plain) } });
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"errors\":[\"cipher: message authentication failed\"]}", Encoding.UTF8, "application/json"),
                    };
                }
            }

            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}
