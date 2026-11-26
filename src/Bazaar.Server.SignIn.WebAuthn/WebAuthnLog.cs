using Microsoft.Extensions.Logging;

namespace Bazaar.Server.SignIn.WebAuthn;

/// <summary>Log messages. Arguments are exception type names and record ids only: never keys, challenges, codes or credential ids.</summary>
internal static partial class WebAuthnLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Passkey assertion refused: malformed input ({Kind}).")]
    public static partial void MalformedAssertion(ILogger logger, string kind);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Passkey assertion refused: sign count did not advance for credential {CredentialRef}.")]
    public static partial void SignCountRollback(ILogger logger, Guid credentialRef);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Sign-in bootstrap code was not issued ({Kind}).")]
    public static partial void BootstrapNotIssued(ILogger logger, string kind);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Passkey registration rolled back: the audit entry could not be written ({Kind}).")]
    public static partial void RegistrationRolledBack(ILogger logger, string kind);
}
