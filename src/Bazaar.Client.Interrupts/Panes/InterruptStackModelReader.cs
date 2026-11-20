using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Interrupts.Panes;

/// <summary>Reads the stack pane model: <c>{ "agents": [ { agentId, name, frames: [ { id, source, reason, state, pushedAt } ] } ] }</c>.</summary>
internal static class InterruptStackModelReader
{
    public static IReadOnlyList<AgentInterruptStack> Read(JsonElement model) =>
        PaneValues.Array(model, "agents").EnumerateArray().Select(ReadAgent).ToArray();

    private static AgentInterruptStack ReadAgent(JsonElement agent)
    {
        if (agent.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each agent must be an object");
        }

        var frames = PaneValues.Array(agent, "frames").EnumerateArray().Select(ReadFrame).ToArray();
        return new AgentInterruptStack(PaneValues.RequiredString(agent, "agentId"), PaneValues.RequiredString(agent, "name"), frames);
    }

    private static InterruptFrameSummary ReadFrame(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each frame must be an object");
        }

        var source = PaneValues.RequiredString(frame, "source") switch
        {
            "owner" => InterruptSource.Owner,
            "system_build" => InterruptSource.SystemBuild,
            "system_security" => InterruptSource.SystemSecurity,
            _ => throw new PaneModelException("unknown interrupt frame source"),
        };
        var state = PaneValues.RequiredString(frame, "state") switch
        {
            "active" => InterruptFrameState.Active,
            "suspended" => InterruptFrameState.Suspended,
            "done" => InterruptFrameState.Done,
            "cancelled" => InterruptFrameState.Cancelled,
            _ => throw new PaneModelException("unknown interrupt frame state"),
        };
        if (!DateTimeOffset.TryParse(PaneValues.RequiredString(frame, "pushedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pushedAt))
        {
            throw new PaneModelException("model field 'pushedAt' must be a timestamp");
        }

        // The reason can be personal text. It is read for display only and never put in an error message or a log.
        return new InterruptFrameSummary(PaneValues.RequiredString(frame, "id"), source, PaneValues.RequiredString(frame, "reason"), state, pushedAt);
    }
}
