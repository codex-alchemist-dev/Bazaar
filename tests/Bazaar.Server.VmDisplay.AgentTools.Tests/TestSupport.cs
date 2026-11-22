using System.Text.Json;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.AgentTools.Tests;

internal sealed class FakeDisplay : IGuestDisplay
{
    public List<CaptureRequest> Captures { get; } = [];

    public List<InputBatch> Inputs { get; } = [];

    public Func<ScreenFrame> Frame { get; set; } = () => Rig.Frame(64, 48);

    public DisplayException? Fail { get; set; }

    public Task<ScreenFrame> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Captures.Add(request);
        if (Fail is not null)
            throw Fail;
        return Task.FromResult(Frame());
    }

    public Task SendInputAsync(InputBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Inputs.Add(batch);
        if (Fail is not null)
            throw Fail;
        return Task.CompletedTask;
    }

    public IReadOnlyList<DisplaySourceKind> AvailableSources(DisplayTarget target) => [DisplaySourceKind.Hypervisor];
}

internal sealed class FakeTargets : IAgentDisplayTargets
{
    public Dictionary<(Guid, AgentDisplayKind), DisplayTarget> Map { get; } = [];

    public List<(Guid Agent, AgentDisplayKind Kind)> Asked { get; } = [];

    public DisplayTarget? Resolve(Guid agentId, AgentDisplayKind kind)
    {
        Asked.Add((agentId, kind));
        return Map.TryGetValue((agentId, kind), out DisplayTarget? t) ? t : null;
    }
}

internal sealed class FakeNative((int, int)? size) : IDisplayNativeSize
{
    public (int Width, int Height)? TryGet(DisplayTarget target) => size;
}

internal static class Rig
{
    public static readonly Guid Agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OtherAgent = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static ToolCallContext Context(Guid? agent = null) => new(agent ?? Agent, Guid.NewGuid(), Guid.NewGuid());

    public static DisplayTarget Target(Guid agent, DisplayGuestOs os, string? browserSession = null) =>
        new(agent.ToString("D"), Guid.NewGuid(), os, "backend", browserSession);

    public static FakeTargets TargetsFor(Guid agent)
    {
        var t = new FakeTargets();
        t.Map[(agent, AgentDisplayKind.Linux)] = Target(agent, DisplayGuestOs.Linux);
        t.Map[(agent, AgentDisplayKind.Windows)] = Target(agent, DisplayGuestOs.Windows);
        t.Map[(agent, AgentDisplayKind.Browser)] = Target(agent, DisplayGuestOs.Linux, "session-1");
        return t;
    }

    public static ScreenFrame Frame(int width, int height, int bytes = 100, DisplaySourceKind source = DisplaySourceKind.Hypervisor)
    {
        byte[] png = new byte[bytes];
        return new ScreenFrame(png, width, height, source, DateTimeOffset.UnixEpoch, ScreenFrame.ComputeSha256(png));
    }

    public static JsonElement Json(string text) => JsonElement.Parse(text);

    public static JsonElement Parse(string result) => JsonElement.Parse(result);
}

internal sealed class FakeReader((int, int)? size) : IDisplayNativeSizeReader
{
    public (int Width, int Height)? TryGetNativeSize(DisplayTarget target) => size;
}
