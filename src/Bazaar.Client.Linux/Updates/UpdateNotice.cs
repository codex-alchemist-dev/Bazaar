using Bazaar.Client.Updater.Linux;

namespace Bazaar.Client.Linux.Updates;

/// <summary>What a check ended in, as the user sees it.</summary>
public enum UpdateNoticeKind
{
    /// <summary>Updates are off for this install.</summary>
    Disabled,

    /// <summary>A check is running.</summary>
    Checking,

    /// <summary>The installed build is current, or the server offers nothing.</summary>
    UpToDate,

    /// <summary>A release is staged and applies on the next restart.</summary>
    ReadyAfterRestart,

    /// <summary>The check or the install did not finish; the text says why.</summary>
    Failed,
}

/// <summary>One short notice about updates.</summary>
/// <param name="Kind">The outcome.</param>
/// <param name="Text">The line to show.</param>
/// <param name="Version">The staged version for <see cref="UpdateNoticeKind.ReadyAfterRestart"/>, else null.</param>
public sealed record UpdateNotice(UpdateNoticeKind Kind, string Text, string? Version = null)
{
    /// <summary>Maps an updater result. A staged release is never applied to the running process.</summary>
    public static UpdateNotice From(UpdateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            UpdateStatus.Installed => new UpdateNotice(UpdateNoticeKind.ReadyAfterRestart, $"Update {result.Version} is ready. It applies the next time you restart Bazaar.", result.Version),
            UpdateStatus.UpToDate => new UpdateNotice(UpdateNoticeKind.UpToDate, "Bazaar is up to date."),
            UpdateStatus.NoManifest => new UpdateNotice(UpdateNoticeKind.UpToDate, "Your company server offers no update."),
            _ => new UpdateNotice(UpdateNoticeKind.Failed, $"Update not installed: {result.Status}. {result.Detail}".TrimEnd()),
        };
    }
}
