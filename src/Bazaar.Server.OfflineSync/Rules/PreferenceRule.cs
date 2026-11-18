using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>Decides which preference keys are safety-relevant (for example notification rules).</summary>
public interface ISafetyKeyPolicy
{
    /// <summary>Whether a conflicting change to this key must be held for review instead of applied.</summary>
    /// <param name="key">The preference key.</param>
    bool IsSafetyRelevant(string key);
}

/// <summary>
/// Merge rule for user preferences (doc 52 section 5, item 5). Last writer wins per key by HLC. A
/// safety-relevant key with a conflicting change (a different value and a base version that is not the
/// current version) is held for review with outcome <see cref="OfflineOutcomeKind.HeldForReview"/>.
/// </summary>
public sealed class PreferenceRule : LastWriterWinsRule
{
    private readonly ISafetyKeyPolicy _safety;
    private readonly IReviewSink _review;

    /// <summary>Creates the rule.</summary>
    /// <param name="state">The last-writer-wins state.</param>
    /// <param name="safety">Marks safety-relevant keys.</param>
    /// <param name="review">Receives held changes.</param>
    public PreferenceRule(ILastWriterWinsState state, ISafetyKeyPolicy safety, IReviewSink review)
        : base(state)
    {
        _safety = safety;
        _review = review;
    }

    /// <inheritdoc />
    public override OfflineDataClass DataClass => OfflineDataClass.Preference;

    /// <inheritdoc />
    protected override bool IsWellFormed(ReadOnlyMemory<byte> payload) =>
        RulePayloadCodec.ReadPreference(payload) is not null;

    /// <inheritdoc />
    protected override async ValueTask<OfflineWriteOutcome?> CheckAsync(OfflineWrite write, LwwEntry? current, CancellationToken cancellationToken)
    {
        if (current is null || write.BaseVersion == current.Version || !_safety.IsSafetyRelevant(write.Target))
        {
            return null;
        }

        var incoming = RulePayloadCodec.ReadPreference(write.Payload);
        var existing = RulePayloadCodec.ReadPreference(current.Payload);
        if (incoming is null || existing?.Value == incoming.Value)
        {
            return null;
        }

        const string reason = "Safety-relevant preference changed on two devices; held for review.";
        await _review.OpenAsync(new ReviewItem("preference-held", write.Target, write.WriteId, write.BaseVersion, current.Version, reason), cancellationToken);
        return new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.HeldForReview, current.Version, reason);
    }
}
