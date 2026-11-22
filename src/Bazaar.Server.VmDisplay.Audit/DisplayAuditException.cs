namespace Bazaar.Server.VmDisplay.Audit;

/// <summary>
/// An audit row could not be written. The message never carries display data. The display module treats a failure on a capture as
/// a failed source, so an unaudited frame is never returned (docs/design/59, section 5, rule 6).
/// </summary>
public sealed class DisplayAuditException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DisplayAuditException(string message)
        : base(message)
    {
    }
}
