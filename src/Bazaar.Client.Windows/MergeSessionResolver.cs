using Bazaar.Client.GitGui.Merge;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.Windows;

/// <summary>
/// Turns the saved <see cref="ClientSession"/> into the merge button's <see cref="MergePrincipal"/> by asking the server who the
/// token belongs to (<c>GET /auth/whoami</c>, doc 32 section 5; doc 52, Merge identity). Any refusal or failure gives no principal,
/// so the merge card says this client cannot approve. It also keeps the device id the server reports for the session, which is the
/// id the sign-in handoff created and the approval is signed for (doc 32 section 5b).
/// </summary>
public sealed class MergeSessionResolver(IWhoAmIClient whoAmI, Func<Guid, string>? emailFor = null)
{
    /// <summary>
    /// The interim email shape for the sign-off line. No profile field holds a person's email yet, so this follows the GitHub
    /// noreply form that doc 17 uses for agent commit authors (<c>IAgentGitIdentity</c>: no domain built in there, the operator
    /// sets it). Until an email profile field exists (PROPOSED, doc 52), this one constant is the only place that shape lives.
    /// </summary>
    public const string InterimEmailDomain = "users.noreply.github.com";

    private readonly IWhoAmIClient _whoAmI = whoAmI ?? throw new ArgumentNullException(nameof(whoAmI));
    private readonly Func<Guid, string> _emailFor = emailFor ?? InterimEmail;

    /// <summary>The device id from the last successful lookup, or null when there was none.</summary>
    public Guid? DeviceId { get; private set; }

    /// <summary>The interim email for a person: their id (no dashes) at the noreply address.</summary>
    public static string InterimEmail(Guid humanId) => $"{humanId:N}@{InterimEmailDomain}";

    /// <summary>The principal for the session, or null when the server did not name one.</summary>
    public async Task<MergePrincipal?> ResolveAsync(ClientSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        DeviceId = null;
        WhoAmIResult result;
        try
        {
            result = await _whoAmI.WhoAmIAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }

        if (result.Outcome != WhoAmIOutcome.Found || result.Person is not { } person)
        {
            return null;
        }

        DeviceId = person.DeviceId;
        return new MergePrincipal(person.HumanId, person.DisplayName, _emailFor(person.HumanId));
    }

    /// <summary>
    /// Registers the merge identity and the merge approver for the saved session, off the caller's thread. Any failure, or no
    /// session, leaves both out of the context, so the merge card says this client cannot approve.
    /// </summary>
    public static async Task<PaneModuleContext> ComposeAsync(
        PaneModuleContext context,
        ISessionStore sessions,
        IWhoAmIClient whoAmI,
        IDeviceApprovalPlatform? platform,
        IMergeProfileStore? profile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var resolver = new MergeSessionResolver(whoAmI);
        try
        {
            var withIdentity = await Task.Run(
                () => context.WithOwnerMergeIdentityAsync(sessions, resolver.ResolveAsync, profile, cancellationToken), cancellationToken).ConfigureAwait(false);
            return withIdentity.WithOwnerMergeApprover(platform, withIdentity.Get<IOwnerMergeIdentity>(), resolver.DeviceId);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or InvalidOperationException or IOException)
        {
            return context;
        }
    }
}
