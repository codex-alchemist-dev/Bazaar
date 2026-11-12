using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Core.Ryusei;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The RyuseiFuda micro schedule pane (pane kind <see cref="PaneKinds.RyuseiSchedule"/>, doc 38 layer 2). Model:
/// <c>{ "triggers": [ { "id", "kind", "state", "nextFire", "targetNames": [...], "purpose" } ] }</c>. Each trigger is
/// read as the server sent it; ordering and text come from <see cref="RyuseiMicroPaneViewModel"/>. A missing field or a
/// bad timestamp raises <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
/// <remarks>
/// Pause and resume go through <see cref="IRyuseiTriggerActions"/>. Without one the buttons stay disabled. After a
/// successful change the row takes the new state locally; the server's next descriptor replaces the list anyway. A
/// refused or failed change keeps the row and sets <see cref="LastError"/>.
/// </remarks>
public sealed class RyuseiSchedulePaneViewModel : IPaneViewModel
{
    private const string _notConnected = "Trigger changes are not connected to a server.";
    private const string _notSaved = "The change was not saved. Try again.";

    private readonly IRyuseiTriggerActions? _actions;
    private readonly List<RyuseiTriggerSummary> _triggers;

    public RyuseiSchedulePaneViewModel(PaneDescriptor descriptor, IRyuseiTriggerActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = RyuseiMicroPaneViewModel.Title;
        _actions = actions;
        _triggers = ReadTriggers(descriptor.Model);
        Schedule = new RyuseiMicroPaneViewModel(_triggers);
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The ordered rows the view binds to. Rebuilt after each successful change.</summary>
    public RyuseiMicroPaneViewModel Schedule { get; private set; }

    /// <summary>True when a head supplied the trigger actions, so Pause and Resume can run.</summary>
    public bool CanChangeTriggers => _actions is not null;

    /// <summary>The reason the last change did not happen, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Pauses one trigger. The row shows Paused only after the actions succeed.</summary>
    public Task PauseAsync(string triggerId, CancellationToken ct = default) =>
        ChangeAsync(triggerId, "paused", (actions, id, token) => actions.PauseAsync(id, token), ct);

    /// <summary>Resumes one paused trigger. The row shows Active only after the actions succeed.</summary>
    public Task ResumeAsync(string triggerId, CancellationToken ct = default) =>
        ChangeAsync(triggerId, "active", (actions, id, token) => actions.ResumeAsync(id, token), ct);

    private async Task ChangeAsync(
        string triggerId,
        string newState,
        Func<IRyuseiTriggerActions, string, CancellationToken, Task> change,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(triggerId);

        if (_actions is null)
        {
            LastError = _notConnected;
            return;
        }

        try
        {
            await change(_actions, triggerId, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The reason is not shown: it can carry server text, and the row must not show a state the server did not confirm.
            LastError = _notSaved;
            return;
        }

        for (var i = 0; i < _triggers.Count; i++)
        {
            if (string.Equals(_triggers[i].Id, triggerId, StringComparison.Ordinal))
            {
                _triggers[i] = _triggers[i] with { State = newState };
            }
        }

        Schedule = new RyuseiMicroPaneViewModel(_triggers);
        LastError = null;
    }

    private static List<RyuseiTriggerSummary> ReadTriggers(JsonElement model)
    {
        var result = new List<RyuseiTriggerSummary>();
        foreach (var element in PaneValues.Array(model, "triggers").EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new PaneModelException("each trigger must be an object");
            }

            DateTimeOffset? nextFire = null;
            if (element.TryGetProperty("nextFire", out var fire) && fire.ValueKind != JsonValueKind.Null)
            {
                if (fire.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(fire.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    throw new PaneModelException("trigger 'nextFire' must be an ISO timestamp or null");
                }

                nextFire = parsed;
            }

            var targets = new List<string>();
            foreach (var name in PaneValues.Array(element, "targetNames").EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String)
                {
                    throw new PaneModelException("trigger 'targetNames' must hold strings");
                }

                targets.Add(name.GetString()!);
            }

            result.Add(new RyuseiTriggerSummary(
                PaneValues.RequiredString(element, "id"),
                PaneValues.RequiredString(element, "kind"),
                PaneValues.RequiredString(element, "state"),
                nextFire,
                targets,
                PaneValues.RequiredString(element, "purpose")));
        }

        return result;
    }
}
