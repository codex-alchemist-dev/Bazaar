namespace Bazaar.Server.BrowserPool.Display;

/// <summary>Why a page driver call failed.</summary>
public enum PageDriverFailure
{
    /// <summary>The pool has no DevTools pipe for the session (unknown, ended, or started without one).</summary>
    NoPipe,

    /// <summary>The pipe ended or its framing failed; every call waiting on it failed too.</summary>
    PipeClosed,

    /// <summary>The browser answered a command with an error.</summary>
    ProtocolError,

    /// <summary>Too many commands were already waiting for a reply.</summary>
    Busy,

    /// <summary>The browser has no page target to drive.</summary>
    NoPage,

    /// <summary>The screenshot is larger than the limit.</summary>
    TooLarge,

    /// <summary>A reply was not the shape the driver expects.</summary>
    BadResponse,
}

/// <summary>
/// A page driver call failed. The message names the kind of failure and, for a protocol error, the browser's numeric code. It never
/// carries typed text, page content, screenshot bytes or the browser's own error text.
/// </summary>
public sealed class PageDriverException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PageDriverException(PageDriverFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    /// <summary>The failure kind.</summary>
    public PageDriverFailure Failure { get; }

    /// <summary>The browser's numeric error code for <see cref="PageDriverFailure.ProtocolError"/>, otherwise null.</summary>
    public int? ProtocolCode { get; init; }
}
