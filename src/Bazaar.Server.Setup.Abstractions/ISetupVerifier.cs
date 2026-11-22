namespace Bazaar.Server.Setup;

/// <summary>Runs the verification checks over a plan and reports the result (doc 45 section 1.12). Read-only.</summary>
public interface ISetupVerifier
{
    /// <summary>Runs every check, in order, and returns the report. A check that fails does not stop the others.</summary>
    Task<SetupVerificationReport> VerifyAsync(SetupVerificationContext context, CancellationToken cancellationToken = default);
}
