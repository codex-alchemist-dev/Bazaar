using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// The exit codes of <c>kodo verify</c> (doc 45 section 1.12). Green, invalid input and amber reuse the setup scheme's numbers. Red is 3,
/// the number the setup scheme gives to a failed and rolled-back apply. The help text states this, so a script reads the command's own
/// meaning, not the apply's.
/// </summary>
public static class KodoVerifyExitCodes
{
    /// <summary>Every check is green, and no validator hold is present.</summary>
    public const int Green = (int)SetupExitCode.Ok;

    /// <summary>The input is invalid: a schema, type, reference or validation error. No checks ran.</summary>
    public const int InvalidInput = (int)SetupExitCode.InvalidInput;

    /// <summary>Amber: some check needs acknowledgement, or a validator hold is present. Setup completes only with each amber item acknowledged.</summary>
    public const int Amber = (int)SetupExitCode.Held;

    /// <summary>Red: a check failed, so setup cannot be marked complete.</summary>
    public const int Red = (int)SetupExitCode.ApplyFailedRolledBack;

    /// <summary>
    /// The exit code for an overall status. A validator hold (exit 2 in setup validate) makes a green report amber here, so an operator
    /// who passes a plan with a hold cannot see a green exit.
    /// </summary>
    public static int For(SetupCheckStatus overall, bool hasValidatorHold) => overall switch
    {
        SetupCheckStatus.Red => Red,
        SetupCheckStatus.Amber or SetupCheckStatus.NotEvaluated => Amber,
        _ => hasValidatorHold ? Amber : Green,
    };
}
