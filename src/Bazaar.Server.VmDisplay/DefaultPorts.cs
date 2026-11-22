using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay;

/// <summary>The fail-closed gate registered until the host supplies the real one (docs/design/15 taint and kill switch).</summary>
internal sealed class UnconfiguredDisplayGate : IDisplayGate
{
    private static readonly DisplayVerdict _refused = DisplayVerdict.Deny("no display gate configured");

    public ValueTask<DisplayVerdict> CheckCaptureAsync(DisplayTarget target, CancellationToken ct) => ValueTask.FromResult(_refused);

    public ValueTask<DisplayVerdict> CheckInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct) => ValueTask.FromResult(_refused);
}

/// <summary>The audit sink registered until the host supplies one: it records nothing.</summary>
internal sealed class NoopDisplayAuditSink : IDisplayAuditSink
{
    public void Captured(DisplayTarget target, DisplaySourceKind source, int width, int height, int bytes, string sha256)
    {
    }

    public void InputSent(DisplayTarget target, DisplaySourceKind source, int actionCount)
    {
    }

    public void Refused(DisplayTarget target, string operation, string reason)
    {
    }
}
