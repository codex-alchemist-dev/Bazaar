using Bazaar.Contracts.Enrolment;

namespace Bazaar.Client.DeviceEnrolment;

/// <summary>Who this device is and how it authenticates (doc 32 section 5b). The server takes both ids from the token it issued; these are what the client believes.</summary>
public interface IEnrolmentSession
{
    /// <summary>The signed-in human.</summary>
    Guid HumanId { get; }

    /// <summary>The signed-in device.</summary>
    Guid DeviceId { get; }

    /// <summary>The device class on the wire: <c>windows</c>, <c>linux</c>, <c>android</c>, <c>terminal</c> or <c>other</c>.</summary>
    string DeviceClass { get; }

    /// <summary>The current bearer access token, refreshed by the sign-in module.</summary>
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>A platform credential created for enrolment (doc 48 section 8.1): a WebAuthn registration on Windows (Hello) or Android.</summary>
public sealed record PlatformCredential(byte[] CredentialId, byte[] AttestationObject, byte[] ClientDataJson);

/// <summary>Creates the platform credential, signing <paramref name="challenge"/> as its WebAuthn challenge. Implemented per platform; none ships yet.</summary>
public interface IPlatformCredentialCreator
{
    /// <summary>Returns null when the person cancelled or the platform has no usable authenticator.</summary>
    ValueTask<PlatformCredential?> CreateAsync(byte[] challenge, Guid humanId, Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>The proof that the owner started this enrolment: exactly one of the two members is set.</summary>
public sealed record EnrolmentStarter(string? ConsoleCode, EnrolmentApprovalDto? Approval)
{
    /// <summary>First device: the one-time code the server console showed.</summary>
    public static EnrolmentStarter FromConsoleCode(string code) => new(code, null);

    /// <summary>Further device: an owner-signed <c>device.enrol</c> approval from an enrolled device.</summary>
    public static EnrolmentStarter FromApproval(EnrolmentApprovalDto approval) => new(null, approval);
}

/// <summary>
/// Gets the starter proof. For a console code it prompts the person. For an approval it must hand the new device id and the
/// SHA-256 of the new approval key to an enrolled device and wait for its signature; how that hand-over travels is not decided
/// by the design yet, so it stays behind this interface.
/// </summary>
public interface IEnrolmentStarterSource
{
    /// <summary>Returns null when the person cancelled or no starter arrived.</summary>
    ValueTask<EnrolmentStarter?> GetAsync(Guid newDeviceId, byte[] approvalPublicKeySpki, CancellationToken cancellationToken);
}

/// <summary>How an enrolment attempt ended.</summary>
public enum EnrolmentClientOutcome
{
    Enrolled,

    /// <summary>The server refused. <see cref="EnrolmentClientResult.Reason"/> carries its fixed code.</summary>
    Refused,

    /// <summary>The server or network was not available, or sent an unreadable reply. Safe to retry with a new challenge.</summary>
    Unavailable,

    /// <summary>The person cancelled the credential or starter step. Nothing was sent.</summary>
    Cancelled,

    /// <summary>No hardware approval key could be made on this device. Nothing was sent.</summary>
    NoHardwareKey,
}

/// <summary>The result of <see cref="IDeviceEnrolmentClient.EnrolAsync"/>.</summary>
public sealed record EnrolmentClientResult(EnrolmentClientOutcome Outcome, string Reason, bool CredentialAttested = false)
{
    public bool Succeeded => Outcome == EnrolmentClientOutcome.Enrolled;
}

/// <summary>Drives the device side of the enrolment ceremony (doc 32 section 5b).</summary>
public interface IDeviceEnrolmentClient
{
    /// <summary>Asks for a challenge, creates the approval key and the credential, gets the starter proof and sends the request.</summary>
    Task<EnrolmentClientResult> EnrolAsync(CancellationToken cancellationToken = default);
}
