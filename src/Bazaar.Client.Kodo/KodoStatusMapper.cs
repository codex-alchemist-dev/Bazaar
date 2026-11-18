using Bazaar.Contracts.Kodo;

namespace Bazaar.Client.Kodo;

/// <summary>
/// Maps the wire status into the view model's snapshot. Wire spellings are checked exactly (doc 46, section 8.1). An
/// unknown state or control is an error, never a silent default.
/// </summary>
public static class KodoStatusMapper
{
    /// <summary>Maps one wire status. Returns false with a reason when the status cannot be read.</summary>
    public static bool TryMap(KodoStatusDto dto, out KodoStatusSnapshot? snapshot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(dto);

        snapshot = null;
        if (!TryState(dto.State, out var state))
        {
            error = $"Unknown Kodo state '{dto.State}'.";
            return false;
        }

        KodoHealthState? underlying = null;
        if (dto.UnderlyingState is not null)
        {
            if (!TryState(dto.UnderlyingState, out var parsed))
            {
                error = $"Unknown underlying Kodo state '{dto.UnderlyingState}'.";
                return false;
            }

            underlying = parsed;
        }

        var controls = new List<KodoControlState>(dto.Controls.Count);
        foreach (var control in dto.Controls)
        {
            if (!TryControl(control.Control, out var parsedControl))
            {
                error = $"Unknown Kodo control '{control.Control}'.";
                return false;
            }

            controls.Add(new KodoControlState(parsedControl, control.Enabled, control.DisabledReason));
        }

        snapshot = new KodoStatusSnapshot(state, underlying, dto.Score, dto.AsOf, controls);
        error = null;
        return true;
    }

    private static bool TryState(string wire, out KodoHealthState state)
    {
        switch (wire)
        {
            case "healthy": state = KodoHealthState.Healthy; return true;
            case "degraded": state = KodoHealthState.Degraded; return true;
            case "failing": state = KodoHealthState.Failing; return true;
            case "recovering": state = KodoHealthState.Recovering; return true;
            case "frozen": state = KodoHealthState.Frozen; return true;
            default: state = default; return false;
        }
    }

    private static bool TryControl(string wire, out KodoControl control)
    {
        switch (wire)
        {
            case "restart": control = KodoControl.Restart; return true;
            case "isolate": control = KodoControl.Isolate; return true;
            case "rollback": control = KodoControl.RollBack; return true;
            case "incidentExit": control = KodoControl.IncidentExit; return true;
            default: control = default; return false;
        }
    }
}
