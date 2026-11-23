using Bazaar.Server.Council.Pilot;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Council.Settings;
using Bazaar.Server.Knowledge;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.CouncilAdapters;

/// <summary>
/// Builds <see cref="CouncilOptions"/> and <see cref="PilotSettings"/> from the <c>council.*</c> settings (doc 10, Settings). The
/// host reads them when the Council service is first resolved, so a change to these keys applies at the next restart. Only
/// <c>council.followup_dissent_share</c> is live per tally, through <see cref="SettingsCouncilDissentShareSource"/>, as the doc says.
/// Options values pass through <see cref="CouncilOptions.Validate"/>, so a value the options reject fails the read.
/// </summary>
public sealed class SettingsCouncilOptionsProvider(ISettingStore settings)
{
    /// <summary>Reads the settings and returns validated options.</summary>
    public async Task<CouncilOptions> LoadAsync(CancellationToken ct = default)
    {
        var options = new CouncilOptions
        {
            QuorumMin = await settings.GetAsync(CouncilSettings.QuorumMin, cancellationToken: ct).ConfigureAwait(false),
            VoteExtensionsMax = await settings.GetAsync(CouncilSettings.VoteExtensionsMax, cancellationToken: ct).ConfigureAwait(false),
            VoteDeadlineCapHours = await settings.GetAsync(CouncilSettings.VoteDeadlineCapHours, cancellationToken: ct).ConfigureAwait(false),
            OpenVotesPerAgentDay = await settings.GetAsync(CouncilSettings.OpenVotesPerAgentDay, cancellationToken: ct).ConfigureAwait(false),
            ReopenCooldownHours = await settings.GetAsync(CouncilSettings.ReopenCooldownHours, cancellationToken: ct).ConfigureAwait(false),
            UnanimityElectorate = await settings.GetAsync(CouncilSettings.UnanimityElectorateKey, cancellationToken: ct).ConfigureAwait(false),
            ExcludeAuthor = await settings.GetAsync(CouncilSettings.ExcludeAuthor, cancellationToken: ct).ConfigureAwait(false),
            ExcludeMembershipSubject = await settings.GetAsync(CouncilSettings.ExcludeMembershipSubject, cancellationToken: ct).ConfigureAwait(false),
            OwnerVotes = await settings.GetAsync(CouncilSettings.OwnerVotes, cancellationToken: ct).ConfigureAwait(false),
            FollowupEnabled = await settings.GetAsync(CouncilSettings.FollowupEnabled, cancellationToken: ct).ConfigureAwait(false),
            FollowupDissentShare = await settings.GetAsync(KnowledgeSettings.CouncilFollowupDissentShareKey, cancellationToken: ct).ConfigureAwait(false),
            ReasonMinSentences = await settings.GetAsync(CouncilSettings.ReasonMinSentences, cancellationToken: ct).ConfigureAwait(false),
            ReasonMaxChars = await settings.GetAsync(CouncilSettings.ReasonMaxChars, cancellationToken: ct).ConfigureAwait(false),
            ReasonOrder = await settings.GetAsync(CouncilSettings.ReasonOrderKey, cancellationToken: ct).ConfigureAwait(false),
        };
        options.Validate();
        return options;
    }

    /// <summary>Reads the pilot settings.</summary>
    public async Task<PilotSettings> LoadPilotAsync(CancellationToken ct = default) => new(
        await settings.GetAsync(CouncilSettings.PilotHoldoutShare, cancellationToken: ct).ConfigureAwait(false),
        await settings.GetAsync(CouncilSettings.PilotStopFlipShare, cancellationToken: ct).ConfigureAwait(false),
        await settings.GetAsync(CouncilSettings.PilotTargetVotes, cancellationToken: ct).ConfigureAwait(false));
}
