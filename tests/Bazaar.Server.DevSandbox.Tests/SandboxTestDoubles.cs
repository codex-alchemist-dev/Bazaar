using System.Runtime.CompilerServices;

namespace Bazaar.Server.DevSandbox.Tests;

internal sealed class FakeProcess : ISandboxProcess
{
    public int Kills { get; private set; }

    public int Disposals { get; private set; }

    public Task KillAsync(CancellationToken ct = default)
    {
        Kills++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeProvider(SandboxOs os, bool tested = true) : ISandboxConfinementProvider
{
    public SandboxOs Os { get; } = os;

    public bool Tested { get; } = tested;

    public int Starts { get; private set; }

    public FakeProcess? Last { get; private set; }

    public Task<ISandboxProcess> StartAsync(SandboxRunSpec spec, CancellationToken ct = default)
    {
        Starts++;
        Last = new FakeProcess();
        return Task.FromResult<ISandboxProcess>(Last);
    }
}

internal sealed class ScriptedEngine(IReadOnlyList<SandboxAction> actions, Exception? throwAfter = null, bool neverEnd = false) : ISandboxEngine
{
    public SandboxEngineKind Kind => SandboxEngineKind.Wasm;

    public int Runs { get; private set; }

    public async IAsyncEnumerable<SandboxAction> RunAsync(
        SandboxRunSpec spec, ISandboxProcess process, SandboxTestCompany company, [EnumeratorCancellation] CancellationToken ct)
    {
        Runs++;
        foreach (var a in actions)
        {
            await Task.Yield();
            yield return a;
        }

        if (throwAfter is not null)
        {
            throw throwAfter;
        }

        if (neverEnd)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}

internal sealed class FakeCompanyFactory : ISandboxCompanyFactory
{
    public const string Canary = "CANARY-ABC123";

    public string Schema { get; init; } = "sandbox_run_1";

    public SandboxTestCompany Create(SandboxRunSpec spec) =>
        new("sandbox-" + spec.RunId, Schema, Schema, new Dictionary<string, string>(), [Canary]);
}

internal static class Specs
{
    public static SandboxRunSpec Valid(string? targetCompany = null, int timeout = 30) => new(
        "run-1",
        "mod.example",
        "1.0.0",
        SandboxEngineKind.Wasm,
        [new SandboxPermissionGrant("fs.read", "/data/ok.txt"), new SandboxPermissionGrant("net.egress", "svc.example.test")],
        ["svc.example.test"],
        42UL,
        timeout,
        256,
        targetCompany);

    public static SandboxAction GoodFile { get; } = new(SandboxActionKind.FileAccess, "fs.read", "/data/ok.txt");

    public static SandboxAction GoodNet { get; } = new(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", "hello");

    public static SandboxAction BadFile { get; } = new(SandboxActionKind.FileAccess, "fs.read", "/etc/passwd");

    public static SandboxAction BadNet { get; } = new(SandboxActionKind.NetworkAttempt, "net.egress", "evil.example", "hello");
}
