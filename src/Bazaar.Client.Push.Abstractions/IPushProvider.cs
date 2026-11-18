namespace Bazaar.Client.Push;

/// <summary>
/// A push transport for content-free wake-ups (doc 29, Background delivery). A provider carries no message content:
/// a wake is a kind only, and the client fetches over the authenticated connection (DECISIONS-LOG round 5, C41).
/// </summary>
public interface IPushProvider
{
    /// <summary>The setting value of <c>android.push_provider</c>: <c>unifiedpush</c> or <c>fcm</c>.</summary>
    string Name { get; }

    /// <summary>Registers this device with the provider and returns the token the server stores (<c>device.registerPush</c>).</summary>
    Task<string> RegisterAsync(CancellationToken ct);
}
