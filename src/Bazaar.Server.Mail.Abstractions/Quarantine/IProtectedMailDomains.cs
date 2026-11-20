namespace Bazaar.Server.Mail.Quarantine;

/// <summary>
/// The domains the lookalike-sender screen guards (doc 22, PROPOSED setting <c>mail.protected_domains</c>): the operator's own
/// mail domains plus any extra list the caller supplies. Nothing is built in, so an install with no domains and no extras
/// turns the screen off.
/// </summary>
public interface IProtectedMailDomains
{
    /// <summary>The protected set: the operator's mail domains and <paramref name="extraDomains"/>, lowercased, distinct, in ordinal order.</summary>
    /// <param name="extraDomains">Extra domains from settings; blank entries are ignored.</param>
    /// <param name="ct">Cancellation.</param>
    Task<IReadOnlyList<string>> GetAsync(IReadOnlyCollection<string> extraDomains, CancellationToken ct = default);
}
