using System.Globalization;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Agents.Turns.Context;

/// <summary>Settings for <see cref="GcContextSource"/>. Defaults are the safe ones: a small backread and a per-message cap.</summary>
public sealed record GcContextOptions
{
    /// <summary>Most recent messages of the trigger GC that go into the backread. The trigger message is always included.</summary>
    public int BackreadMessages { get; init; } = 20;

    /// <summary>Longest text kept per message. Longer text is cut, so one message cannot fill the backread budget.</summary>
    public int MaxMessageChars { get; init; } = 1000;
}

/// <summary>
/// The trigger GC's recent messages (<see cref="ContextSection.GcBackread"/>) and the trigger message verbatim
/// (<see cref="ContextSection.Inbox"/>), read as the agent's own principal (decision: Round 23).
/// </summary>
/// <remarks>
/// Membership is the rule. Every read asks <see cref="IGcReadAccess"/> for the agent's grant, so an agent sees only the GCs it may
/// read, and only up to its <c>left_seq</c>. A GC the agent may not read still gets a backread item, with a fixed notice, because the
/// assembler must always find the trigger thread in the backread (doc 06).
/// </remarks>
public sealed class GcContextSource(IGcStreamSource stream, IGcReadAccess access, GcContextOptions options) : IContextSource
{
    private const string _denied = "(no readable history for this GC)";

    private readonly IGcStreamSource _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly IGcReadAccess _access = access ?? throw new ArgumentNullException(nameof(access));
    private readonly GcContextOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextSourceItem>> ReadAsync(TurnContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TriggerGcId is null || !Guid.TryParse(request.TriggerGcId, out var gcId))
            return [];

        var viewer = new GcPrincipal(request.AgentId, PrincipalKind.Agent, "agent");
        var grant = await _access.GetGrantAsync(gcId, viewer, cancellationToken).ConfigureAwait(false);
        if (!grant.Allowed)
            return [new ContextSourceItem(gcId.ToString("D"), "0", _denied, ContextSection.GcBackread)];

        var tail = await _stream.ReadTailAsync(gcId, _options.BackreadMessages, cancellationToken).ConfigureAwait(false);
        var visible = grant.ThroughSeq is { } through
            ? tail.Where(message => message.N <= through).ToList()
            : tail.ToList();

        var items = new List<ContextSourceItem>();
        var latest = visible.Count > 0 ? visible[^1].N : 0;
        items.Add(new ContextSourceItem(
            gcId.ToString("D"),
            latest.ToString(CultureInfo.InvariantCulture),
            visible.Count > 0 ? Format(visible) : _denied,
            ContextSection.GcBackread));

        if (request.TriggerGcSeq is { } triggerSeq)
        {
            var trigger = visible.FirstOrDefault(message => message.N == triggerSeq)
                ?? await FindAsync(gcId, triggerSeq, grant, cancellationToken).ConfigureAwait(false);
            if (trigger is not null)
            {
                items.Add(new ContextSourceItem(
                    trigger.MessageId.ToString("D"),
                    trigger.N.ToString(CultureInfo.InvariantCulture),
                    Format([trigger]),
                    ContextSection.Inbox));
            }
        }

        return items;
    }

    /// <summary>The trigger message when it is older than the backread tail. Reads from just before it, so one row comes back.</summary>
    private async Task<WireGcMessage?> FindAsync(Guid gcId, long seq, GcReadGrant grant, CancellationToken ct)
    {
        if (grant.ThroughSeq is { } through && seq > through)
            return null;

        var page = await _stream.ReadAsync(gcId, seq - 1, 0, 0, 1, ct).ConfigureAwait(false);
        return page.Count > 0 && page[0].N == seq ? page[0] : null;
    }

    private string Format(IEnumerable<WireGcMessage> messages) =>
        string.Join('\n', messages.Select(message =>
        {
            var text = message.Text.Length > _options.MaxMessageChars
                ? message.Text[.._options.MaxMessageChars]
                : message.Text;
            return $"[{message.N.ToString(CultureInfo.InvariantCulture)}] {message.AuthorId:D}: {text}";
        }));
}
