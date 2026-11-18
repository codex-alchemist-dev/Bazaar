using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Hive.Grants;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Hive.Panes;

/// <summary>
/// The Hive pane (pane kind <see cref="PaneKinds.Hive"/>, doc 25 "Modules and client panels"). The server sends the owner
/// grant state and the copy list as data; this view model only formats it. Model:
/// <c>{ "reviewDays", "grants": [ { "fromProject", "toProject", "kind", "grantedBy" } ],
/// "copies": [ { "path", "originProject", "originKind", "trust", "state": "inReview" | "active" | "deprecated", "reviewUntil" } ] }</c>.
/// <c>reviewUntil</c> is required for an <c>inReview</c> copy. A missing required field, or an unknown state, raises
/// <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class HivePaneViewModel : IPaneViewModel
{
    /// <summary>The notice the pane always shows: Hive copies are copies, never shared memory.</summary>
    public const string CopiesOnlyNotice =
        "Copies only. Hive copies are never shared memory: each copy is a labelled snapshot of one lesson from another project.";

    private static readonly string[] _states = ["inReview", "active", "deprecated"];

    private readonly IHiveGrantActions? _actions;

    /// <param name="descriptor">The server's pane descriptor.</param>
    /// <param name="actions">The owner's grant actions, or null when the head supplies none (the buttons then stay disabled).</param>
    public HivePaneViewModel(PaneDescriptor descriptor, IHiveGrantActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Hive Mind";
        _actions = actions;

        var model = descriptor.Model;
        ReviewDays = PaneValues.RequiredInt(model, "reviewDays");
        var grants = PaneValues.Array(model, "grants").EnumerateArray().Select(ReadGrant).ToArray();
        Copies = PaneValues.Array(model, "copies").EnumerateArray().Select(ReadCopy).ToArray();
        Matrix = HiveGrantMatrix.Build(grants, Copies.Select(copy => copy.OriginProject));
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The review window in days. Zero keeps every batch under review.</summary>
    public int ReviewDays { get; }

    /// <summary>The owner grant matrix, one grid per kind.</summary>
    public HiveGrantMatrix Matrix { get; }

    /// <summary>Every copy, in server order. Deprecated copies are included.</summary>
    public IReadOnlyList<HiveCopyRow> Copies { get; }

    /// <summary>True when a head supplied grant actions, so the matrix buttons may be enabled.</summary>
    public bool CanChangeGrants => _actions is not null;

    /// <summary>The outcome of the last request, or null before any request.</summary>
    public string? LastStatus { get; private set; }

    /// <summary>
    /// Asks the server to grant or revoke one pair. The pane never changes the matrix itself: the matrix changes only when a
    /// later model shows the server's decision. A refusal or a lost connection leaves the matrix as it was.
    /// </summary>
    public async Task RequestAsync(HiveGrantKey key, bool grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_actions is null)
        {
            LastStatus = "Grant changes are not available in this client.";
            return;
        }

        try
        {
            var result = grant
                ? await _actions.GrantAsync(key, ct).ConfigureAwait(true)
                : await _actions.RevokeAsync(key, ct).ConfigureAwait(true);
            LastStatus = result.Accepted
                ? "Requested. The matrix changes when the server confirms."
                : $"Refused: {result.Reason}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastStatus = "Could not reach the server. The grant is unchanged.";
        }
    }

    private static HiveGrant ReadGrant(JsonElement grant)
    {
        if (grant.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each grant must be an object");
        }

        return new HiveGrant(
            PaneValues.RequiredString(grant, "fromProject"),
            PaneValues.RequiredString(grant, "toProject"),
            PaneValues.RequiredString(grant, "kind"),
            PaneValues.RequiredString(grant, "grantedBy"));
    }

    private static HiveCopyRow ReadCopy(JsonElement copy)
    {
        if (copy.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each copy must be an object");
        }

        var state = PaneValues.RequiredString(copy, "state");
        if (Array.IndexOf(_states, state) < 0)
        {
            throw new PaneModelException($"unknown copy state '{state}'");
        }

        var parsed = state switch
        {
            "inReview" => HiveCopyState.InReview,
            "active" => HiveCopyState.Active,
            _ => HiveCopyState.Deprecated,
        };
        var reviewUntil = parsed == HiveCopyState.InReview
            ? PaneValues.RequiredString(copy, "reviewUntil")
            : null;

        return new HiveCopyRow(
            PaneValues.RequiredString(copy, "path"),
            PaneValues.RequiredString(copy, "originProject"),
            PaneValues.RequiredString(copy, "originKind"),
            PaneValues.RequiredString(copy, "trust"),
            parsed,
            reviewUntil);
    }
}
