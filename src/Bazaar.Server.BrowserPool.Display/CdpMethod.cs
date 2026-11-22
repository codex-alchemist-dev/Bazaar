namespace Bazaar.Server.BrowserPool.Display;

/// <summary>
/// Every DevTools method this module can send. The set is closed on purpose: the session API takes this enum, never a string, so
/// there is no way to send <c>Runtime.evaluate</c>, <c>Page.navigate</c>, <c>Network.*</c>, <c>Browser.*</c> or
/// <c>Target.createTarget</c> through this module. Adding a member is a security-relevant change and needs the allow-list test updated.
/// </summary>
internal enum CdpMethod
{
    /// <summary><c>Target.getTargets</c>: lists the targets, to find the page.</summary>
    TargetGetTargets,

    /// <summary><c>Target.attachToTarget</c> with <c>flatten: true</c>.</summary>
    TargetAttachToTarget,

    /// <summary><c>Page.captureScreenshot</c>.</summary>
    PageCaptureScreenshot,

    /// <summary><c>Input.dispatchMouseEvent</c>.</summary>
    InputDispatchMouseEvent,

    /// <summary><c>Input.dispatchKeyEvent</c>.</summary>
    InputDispatchKeyEvent,

    /// <summary><c>Input.insertText</c>.</summary>
    InputInsertText,
}

/// <summary>The wire names of <see cref="CdpMethod"/>.</summary>
internal static class CdpMethods
{
    /// <summary>The allow-list: the only method names this module ever writes to the pipe.</summary>
    public static IReadOnlyList<string> AllowList { get; } =
    [
        "Target.getTargets",
        "Target.attachToTarget",
        "Page.captureScreenshot",
        "Input.dispatchMouseEvent",
        "Input.dispatchKeyEvent",
        "Input.insertText",
    ];

    /// <summary>The wire name of a method. Throws for a value outside the enum.</summary>
    public static string Name(CdpMethod method) => method switch
    {
        CdpMethod.TargetGetTargets => "Target.getTargets",
        CdpMethod.TargetAttachToTarget => "Target.attachToTarget",
        CdpMethod.PageCaptureScreenshot => "Page.captureScreenshot",
        CdpMethod.InputDispatchMouseEvent => "Input.dispatchMouseEvent",
        CdpMethod.InputDispatchKeyEvent => "Input.dispatchKeyEvent",
        CdpMethod.InputInsertText => "Input.insertText",
        _ => throw new ArgumentOutOfRangeException(nameof(method), "Unknown DevTools method."),
    };

    /// <summary>Whether the method is a browser-level one (sent without a target session).</summary>
    public static bool IsBrowserLevel(CdpMethod method) => method is CdpMethod.TargetGetTargets or CdpMethod.TargetAttachToTarget;
}
