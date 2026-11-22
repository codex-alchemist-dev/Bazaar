namespace Bazaar.Server.WebAdmin.Http;

/// <summary>The panel's own rate-limit bucket (doc 51 section 5; doc 32 section 7).</summary>
public static class AdminRateLimit
{
    public const string Bucket = "admin";
}
