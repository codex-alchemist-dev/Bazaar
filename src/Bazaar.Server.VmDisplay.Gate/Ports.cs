using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Gate;

/// <summary>How a browser session is classified for the display gate (docs/design/15, "Screen tools").</summary>
public enum BrowserSessionClass
{
    /// <summary>A Passport-backed account profile. Typing into it under hard taint is always refused.</summary>
    Account,

    /// <summary>The anonymous browser profile.</summary>
    Anonymous,
}

/// <summary>Classifies the browser session of a display target. The browser module supplies it.</summary>
public interface IBrowserSessionClassifier
{
    /// <summary>Classifies the target's browser session. Only called when <see cref="DisplayTarget.BrowserSessionId"/> is set.</summary>
    ValueTask<BrowserSessionClass> ClassifyAsync(DisplayTarget target, CancellationToken ct);
}

/// <summary>Optional approval port, asked for input under hard taint when the gate runs in approval mode.</summary>
public interface IDisplayInputApproval
{
    /// <summary>Whether the input is approved. A refusal or a failure to decide means no.</summary>
    ValueTask<bool> IsApprovedAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct);
}
