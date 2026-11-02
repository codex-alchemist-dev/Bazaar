using System.Text.Json.Serialization;

namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>Source-generated JSON for the cookie payload, so the encrypted blob format is fixed at build time.</summary>
[JsonSerializable(typeof(BrowserCookie[]))]
internal sealed partial class CookieJsonContext : JsonSerializerContext;
