namespace Bazaar.Server.Setup.Http;

/// <summary>
/// What the provisioning route learned about its caller. <see cref="IsLoopback"/> is the connection's remote address. The
/// principal is set only when a bearer token authenticated. The bootstrap code is whatever the header carried, unverified.
/// </summary>
/// <param name="IsLoopback">True when the connection comes from the loopback address.</param>
/// <param name="Principal">The authenticated principal, or null when no valid bearer token was presented.</param>
/// <param name="BootstrapCode">The bootstrap code header value, or null when absent. Never logged.</param>
public sealed record SetupCaller(bool IsLoopback, SetupPrincipal? Principal, string? BootstrapCode);

/// <summary>Which rule admitted a call.</summary>
public enum SetupAccessKind
{
    /// <summary>No admission. Used only in a refused decision.</summary>
    None,

    /// <summary>Admitted by the one-time bootstrap code, before the owner exists.</summary>
    Bootstrap,

    /// <summary>Admitted because the caller holds the owner role.</summary>
    Owner,
}

/// <summary>
/// Why a call was refused. The route maps <see cref="Unauthenticated"/> to 401 and <see cref="Forbidden"/> to 403. The
/// response never says which condition failed inside a class; the audit row keeps the reason.
/// </summary>
public enum SetupRefusal
{
    /// <summary>No valid credential: loopback and bootstrap code before the owner exists, or a bearer token after.</summary>
    Unauthenticated,

    /// <summary>A valid credential that does not hold the owner role.</summary>
    Forbidden,
}

/// <summary>The gate's answer for one call.</summary>
/// <param name="Allowed">True when the call may run.</param>
/// <param name="Kind">The rule that admitted it. <see cref="SetupAccessKind.None"/> when refused.</param>
/// <param name="Actor">The audit and operator identity of an admitted call, or the refused actor.</param>
/// <param name="Refusal">The status class of a refused call. Null when allowed.</param>
/// <param name="Reason">The internal reason, for the audit row only. Never sent to the client.</param>
public sealed record SetupAccessDecision(bool Allowed, SetupAccessKind Kind, string Actor, SetupRefusal? Refusal, string Reason)
{
    /// <summary>An admitted call.</summary>
    public static SetupAccessDecision Allow(SetupAccessKind kind, string actor, string reason) =>
        new(true, kind, actor, null, reason);

    /// <summary>A refused call.</summary>
    public static SetupAccessDecision Refuse(SetupRefusal refusal, string reason) =>
        new(false, SetupAccessKind.None, SetupHttpConstants.RefusedActor, refusal, reason);
}

/// <summary>
/// The access gate of the provisioning API (doc 45 section 1.9). Before the owner exists, a call needs loopback and the one-time
/// bootstrap code. After the owner exists, a call needs the owner role, and the bootstrap code is never consulted again.
/// </summary>
public interface ISetupAccessGate
{
    /// <summary>Decides one call. Consumes the bootstrap code when it admits a call in bootstrap mode.</summary>
    Task<SetupAccessDecision> EvaluateAsync(SetupCaller caller, CancellationToken cancellationToken = default);
}

/// <summary>Creates, checks and retires the one-time bootstrap code. A durable store is a later slice; tests use an in-memory one.</summary>
public interface IBootstrapCodeStore
{
    /// <summary>Creates a new code, replacing any earlier one, and returns its text once. The store keeps only a digest.</summary>
    Task<string> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares <paramref name="presentedCode"/> in constant time. A match is a successful use: the code is retired, so the next
    /// check fails. No code, or a code that does not match, returns false and changes nothing.
    /// </summary>
    Task<bool> ConsumeAsync(string presentedCode, CancellationToken cancellationToken = default);

    /// <summary>Retires the current code, if any. Used when the owner is created, so no code outlives it.</summary>
    Task RevokeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Answers whether the install already has an owner. The gate reads it on every call.</summary>
public interface ISetupOwnerDirectory
{
    Task<bool> OwnerExistsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The default gate. It fails closed: any condition it does not recognise is a refusal, and a store that throws ends the call
/// with a server error rather than an admission.
/// </summary>
public sealed class SetupAccessGate(IBootstrapCodeStore codes, ISetupOwnerDirectory owners) : ISetupAccessGate
{
    private const string _bootstrapReason = "bootstrap";
    private const string _ownerReason = "owner";

    /// <inheritdoc />
    public async Task<SetupAccessDecision> EvaluateAsync(SetupCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (await owners.OwnerExistsAsync(cancellationToken))
        {
            if (caller.Principal is null)
            {
                return SetupAccessDecision.Refuse(SetupRefusal.Unauthenticated, "no_credential");
            }

            if (string.Equals(caller.Principal.Kind, SetupHttpConstants.OwnerKind, StringComparison.Ordinal))
            {
                return SetupAccessDecision.Allow(SetupAccessKind.Owner, "owner:" + caller.Principal.Id.ToString("D"), _ownerReason);
            }

            return SetupAccessDecision.Refuse(SetupRefusal.Forbidden, "not_owner_role");
        }

        // Before the owner exists the bootstrap code is the only way in, and only from loopback.
        if (!caller.IsLoopback)
        {
            return SetupAccessDecision.Refuse(SetupRefusal.Unauthenticated, "not_loopback");
        }

        if (string.IsNullOrEmpty(caller.BootstrapCode) || !await codes.ConsumeAsync(caller.BootstrapCode, cancellationToken))
        {
            return SetupAccessDecision.Refuse(SetupRefusal.Unauthenticated, "bootstrap_code_refused");
        }

        return SetupAccessDecision.Allow(SetupAccessKind.Bootstrap, SetupHttpConstants.BootstrapActor, _bootstrapReason);
    }
}
