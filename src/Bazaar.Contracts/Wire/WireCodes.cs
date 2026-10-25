namespace Bazaar.Contracts.Wire;

/// <summary>WebSocket close codes (doc 02).</summary>
public static class WireCloseCodes
{
    /// <summary>A frame could not be decoded.</summary>
    public const int BadFrame = 4400;

    /// <summary>Authentication failed or expired.</summary>
    public const int AuthFailed = 4401;

    /// <summary>The device or session was revoked.</summary>
    public const int Revoked = 4403;

    /// <summary>No <c>hello</c> within the timeout.</summary>
    public const int HelloTimeout = 4408;

    /// <summary>No overlap in protocol versions; the client must upgrade.</summary>
    public const int UpgradeRequired = 4426;

    /// <summary>Rate limited.</summary>
    public const int RateLimited = 4429;
}

/// <summary>Codes carried in <see cref="WireError.Code"/>. Lower snake case; clients ignore codes they do not know.</summary>
public static class WireErrorCodes
{
    /// <summary>The request type is not known to this server.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Too many requests; see <see cref="WireError.RetryAfterMs"/>.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>A cursor is ahead of the server's latest, for example after a restore.</summary>
    public const string StaleCursor = "stale_cursor";

    /// <summary>Denied or missing; the two look the same so membership does not leak.</summary>
    public const string NotFound = "not_found";

    /// <summary>The payload failed validation.</summary>
    public const string BadRequest = "bad_request";

    /// <summary>The credential is missing, invalid or expired.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>A server fault; the request may be retried with the same id.</summary>
    public const string Internal = "internal";
}

/// <summary>Per-stream results of a <c>subscribe</c> (<see cref="StreamResult.Status"/>).</summary>
public static class SubscribeStatuses
{
    /// <summary>Subscribed; replay follows.</summary>
    public const string Ok = "ok";

    /// <summary>Not allowed or not found (indistinguishable).</summary>
    public const string Denied = "denied";

    /// <summary>The gap is too large to replay; a <c>resync</c> event follows.</summary>
    public const string Resync = "resync";
}
