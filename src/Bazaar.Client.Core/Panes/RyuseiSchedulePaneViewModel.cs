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
public sealed class RyuseiSchedulePaneViewModel : IPaneViewModel
{
    public RyuseiSchedulePaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = RyuseiMicroPaneViewModel.Title;
        Schedule = new RyuseiMicroPaneViewModel(ReadTriggers(descriptor.Model));
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The ordered rows the view binds to.</summary>
    public RyuseiMicroPaneViewModel Schedule { get; }

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
