namespace Bazaar.Server.MailInbound;

/// <summary>
/// Who may read a stored raw body (doc 22: raw mail is read by reader sessions only; actors never). The owner reads through the owner-signed
/// path. The system pipeline stores and purges bodies encrypted and does not read them. Every <see cref="IMailMessageStore"/> applies this.
/// </summary>
public static class MailBodyAccessPolicy
{
    public static bool CanReadRawBody(MailAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        return accessor.Kind is MailAccessorKind.Reader or MailAccessorKind.Owner;
    }
}
