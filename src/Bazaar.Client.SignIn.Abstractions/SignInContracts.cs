namespace Bazaar.Client.SignIn;

/// <summary>
/// The credentials one sign-in issued to this device (doc 32 section 5): a short-lived access token and the refresh secret that
/// replaces it. Never logged, never shown.
/// </summary>
/// <param name="AccessToken">The bearer token sent with each request.</param>
/// <param name="RefreshToken">The secret that <c>POST /auth/refresh</c> exchanges for a new pair.</param>
/// <param name="AccessTokenExpiresAt">When the access token stops being valid.</param>
public sealed record ClientSession(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt)
{
    /// <summary>Hides both secrets from <c>ToString</c>, so a log line or a test failure cannot leak them.</summary>
    public override string ToString() => "ClientSession { secrets hidden }";
}

/// <summary>Where this device keeps its session between runs. A platform head supplies a protected store (DPAPI, Keystore, libsecret).</summary>
public interface ISessionStore
{
    /// <summary>The saved session, or null when this device has not signed in.</summary>
    Task<ClientSession?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the saved session.</summary>
    Task SaveAsync(ClientSession session, CancellationToken cancellationToken = default);

    /// <summary>Forgets the session (sign out, or a refresh the server refused).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Seals a few bytes so only this person on this device can read them back (DPAPI on Windows, Keystore on Android, the secret
/// service on Linux). A head supplies one; the protected session store uses it and never sees the key.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Seals <paramref name="plain"/> and returns the sealed bytes.</summary>
    byte[] Protect(ReadOnlySpan<byte> plain);

    /// <summary>Opens bytes this protector sealed, or returns null when they cannot be opened (another user, another device, damage).</summary>
    byte[]? Unprotect(ReadOnlySpan<byte> sealedBytes);
}

/// <summary>Opens a web address in the person's own browser. The sign-in page is a browser page, so the client never draws a password or passkey prompt itself.</summary>
public interface IUrlLauncher
{
    /// <summary>Returns false when the address could not be opened.</summary>
    Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default);
}

/// <summary>What the first contact with the company server found.</summary>
public enum ServerReachability
{
    /// <summary>The server answered and reports itself ready.</summary>
    Ready,

    /// <summary>The server answered but is not ready yet (still starting, or a readiness check is red).</summary>
    NotReady,

    /// <summary>The server did not answer, or the endpoint was refused (no pin, plain http off loopback).</summary>
    Unreachable,
}

/// <summary>How a sign-in step ended. The text is for the person; it never repeats a code or a token.</summary>
public enum SignInOutcome
{
    /// <summary>The device now holds a session.</summary>
    SignedIn,

    /// <summary>The server refused the code or the refresh secret. One generic answer, like the server's.</summary>
    Refused,

    /// <summary>The server is off, not reachable, or answered something unexpected.</summary>
    Unavailable,

    /// <summary>The input was not a handoff code.</summary>
    InvalidCode,
}

/// <summary>The outcome of one sign-in step.</summary>
public sealed record SignInResult(SignInOutcome Outcome, string Message)
{
    /// <summary>True when the device is signed in.</summary>
    public bool Succeeded => Outcome == SignInOutcome.SignedIn;
}

/// <summary>
/// The client side of sign-in (doc 32 section 4, doc 45 section 2.6): probe the server, send the person to its sign-in page, and
/// redeem the single-use handoff code the page returns.
/// </summary>
public interface ISignInService
{
    /// <summary>The address of the server's sign-in page for a native client (<c>/signin?audience=native</c>).</summary>
    Uri SignInPageAddress { get; }

    /// <summary>Asks the server whether it is up (<c>GET /health/ready</c>).</summary>
    Task<ServerReachability> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Redeems a handoff code at <c>POST /auth/handoff</c> and saves the session it returns.</summary>
    Task<SignInResult> RedeemHandoffAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Exchanges the saved refresh secret for a new pair (<c>POST /auth/refresh</c>). Refused means the saved session is cleared.</summary>
    Task<SignInResult> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>The saved session, or null when this device is signed out.</summary>
    Task<ClientSession?> CurrentSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the saved session on this device.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>The signed-in person as the server reports them (<c>GET /auth/whoami</c>, doc 32 section 5, doc 45 section 2.6).</summary>
/// <param name="HumanId">The person's id.</param>
/// <param name="DisplayName">The name shown on their account.</param>
/// <param name="Role">The role string the server sent, such as <c>owner</c> or <c>coworker</c>.</param>
/// <param name="DeviceId">The id of the device this session belongs to, the one the sign-in handoff created.</param>
public sealed record SignedInPerson(Guid HumanId, string DisplayName, string Role, Guid DeviceId);

/// <summary>How a whoami call ended.</summary>
public enum WhoAmIOutcome
{
    /// <summary>The server named the person.</summary>
    Found,

    /// <summary>401: the token is not valid (any reason; the server gives one answer). Sign in again.</summary>
    InvalidToken,

    /// <summary>403: the server refuses plaintext off the machine. Not a sign-in problem.</summary>
    Forbidden,

    /// <summary>503, no answer, or a reply that could not be read. Try later.</summary>
    Unavailable,
}

/// <summary>The outcome of one whoami call. <see cref="Person"/> is set only for <see cref="WhoAmIOutcome.Found"/>.</summary>
public sealed record WhoAmIResult(WhoAmIOutcome Outcome, SignedInPerson? Person = null);

/// <summary>Reads the signed-in person from the company server with an access token.</summary>
public interface IWhoAmIClient
{
    /// <summary>Calls <c>GET /auth/whoami</c> with the session's access token. Never throws for a server or network failure.</summary>
    Task<WhoAmIResult> WhoAmIAsync(ClientSession session, CancellationToken cancellationToken = default);
}
