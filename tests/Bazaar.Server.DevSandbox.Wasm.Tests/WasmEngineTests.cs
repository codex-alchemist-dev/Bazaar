namespace Bazaar.Server.DevSandbox.Wasm.Tests;

internal sealed class FakeProcess : ISandboxProcess
{
    public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeProvider(SandboxOs os) : ISandboxConfinementProvider
{
    public SandboxOs Os { get; } = os;

    public bool Tested => true;

    public Task<ISandboxProcess> StartAsync(SandboxRunSpec spec, CancellationToken ct = default) =>
        Task.FromResult<ISandboxProcess>(new FakeProcess());
}

internal sealed class FakeCompanyFactory : ISandboxCompanyFactory
{
    public SandboxTestCompany Create(SandboxRunSpec spec) => Company();

    public static SandboxTestCompany Company() => new(
        "sandbox-run-1",
        "sandbox_run_1",
        "sandbox_run_1",
        new Dictionary<string, string> { ["notes/a"] = "synthetic page", ["/data/ok.txt"] = "synthetic file", ["db-pass"] = "synthetic secret" },
        ["CANARY-XYZ"]);
}

internal sealed class FakeModuleSource(byte[]? bytes) : IWasmModuleSource
{
    public ValueTask<ReadOnlyMemory<byte>?> GetModuleAsync(string moduleId, string moduleVersion, CancellationToken ct) =>
        ValueTask.FromResult(bytes is null ? default(ReadOnlyMemory<byte>?) : new ReadOnlyMemory<byte>(bytes));
}

/// <summary>A scripted runtime: declares the given imports, and when run makes the given calls in order, recording what the host returned.</summary>
internal sealed class FakeRuntime(IReadOnlyList<WasmImport> declared, IReadOnlyList<(string Name, string Arg)> calls) : IWasmRuntime
{
    public bool Instantiated { get; private set; }

    public WasmLimits? Limits { get; private set; }

    public string? Entry { get; private set; }

    public List<WasmImport> Linked { get; } = [];

    public List<string> Results { get; } = [];

    public IReadOnlyList<WasmImport> GetDeclaredImports(ReadOnlyMemory<byte> moduleBytes) => declared;

    public async Task<WasmRunOutcome> RunAsync(
        ReadOnlyMemory<byte> moduleBytes,
        IReadOnlyCollection<WasmImport> linkedImports,
        IWasmHostImports host,
        WasmLimits limits,
        string entryPoint,
        CancellationToken ct)
    {
        Instantiated = true;
        Limits = limits;
        Entry = entryPoint;
        Linked.AddRange(linkedImports);
        foreach (var (name, arg) in calls)
        {
            Results.Add(await host.CallAsync(name, arg, ct).ConfigureAwait(false));
        }

        return new WasmRunOutcome(WasmRunStatus.Completed);
    }
}

public class WasmEngineTests
{
    private static readonly WasmImport _vault = new("bazaar", "vault_read");
    private static readonly WasmImport _http = new("bazaar", "http_fetch");
    private static readonly WasmImport _evil = new("env", "system");

    private static SandboxRunSpec Spec(params SandboxPermissionGrant[] grants) => new(
        "run-1", "mod.example", "1.0.0", SandboxEngineKind.Wasm, grants, ["svc.example.test"], 42UL, 30, 64);

    private static WasmSandboxEngine Engine(FakeRuntime rt, WasmEngineOptions? o = null) =>
        new(rt, new FakeModuleSource([0, 1]), o);

    private static async Task<List<SandboxAction>> DrainAsync(WasmSandboxEngine e, SandboxRunSpec spec)
    {
        var list = new List<SandboxAction>();
        await foreach (var a in e.RunAsync(spec, new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
        {
            list.Add(a);
        }

        return list;
    }

    [Fact]
    public async Task Ungranted_import_is_not_linked_and_module_is_not_instantiated()
    {
        var rt = new FakeRuntime([_vault, _http], []);
        var actions = await DrainAsync(Engine(rt), Spec(new SandboxPermissionGrant("vault.read", "*")));

        var a = Assert.Single(actions);
        Assert.Equal(SandboxActionKind.PermissionCheck, a.Kind);
        Assert.Equal("net.egress", a.Class);
        Assert.Equal("bazaar.http_fetch", a.Target);
        Assert.False(rt.Instantiated);
    }

    [Fact]
    public async Task Import_outside_catalogue_is_refused_even_with_wildcard_grants()
    {
        var rt = new FakeRuntime([_evil], []);
        var actions = await DrainAsync(Engine(rt), Spec(new SandboxPermissionGrant("vault.read", "*")));

        var a = Assert.Single(actions);
        Assert.Equal(SandboxActionKind.PermissionCheck, a.Kind);
        Assert.Equal(WasmImportCatalogue.UnknownImportClass, a.Class);
        Assert.Equal("env.system", a.Target);
        Assert.False(rt.Instantiated);
    }

    [Fact]
    public async Task Granted_calls_yield_actions_with_kind_class_target_and_company_data()
    {
        var rt = new FakeRuntime(
            [_vault, _http, new WasmImport("bazaar", "file_read"), new WasmImport("bazaar", "secret_read")],
            [("bazaar.vault_read", "notes/a"), ("bazaar.http_fetch", "svc.example.test\nbody"), ("bazaar.file_read", "/data/ok.txt"), ("bazaar.secret_read", "db-pass")]);
        var spec = Spec(
            new SandboxPermissionGrant("vault.read", "notes/*"),
            new SandboxPermissionGrant("net.egress", "svc.example.test"),
            new SandboxPermissionGrant("fs.read", "*"),
            new SandboxPermissionGrant("secret.read", "db-pass"));

        var actions = await DrainAsync(Engine(rt), spec);

        Assert.Equal(
            [
                new SandboxAction(SandboxActionKind.HostCall, "vault.read", "notes/a"),
                new SandboxAction(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", "body"),
                new SandboxAction(SandboxActionKind.FileAccess, "fs.read", "/data/ok.txt"),
                new SandboxAction(SandboxActionKind.SecretRead, "secret.read", "db-pass"),
            ],
            actions);
        Assert.Equal(["synthetic page", "stub-response", "synthetic file", "synthetic secret"], rt.Results);
        Assert.Equal(4, rt.Linked.Count);
    }

    [Fact]
    public async Task Denied_action_is_never_serviced()
    {
        var rt = new FakeRuntime([_vault], [("bazaar.vault_read", "notes/a")]);
        var e = Engine(rt);
        var en = e.RunAsync(Spec(new SandboxPermissionGrant("vault.read", "*")), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await en.MoveNextAsync());
        Assert.Equal("notes/a", en.Current.Target);

        // The runner denied it: it stops without asking for the next action.
        await en.DisposeAsync();

        Assert.Empty(rt.Results);
    }

    [Fact]
    public async Task Call_not_covered_by_target_grant_is_withheld_even_if_consumer_continues()
    {
        var rt = new FakeRuntime([_vault], [("bazaar.vault_read", "other/secret")]);
        var spec = Spec(new SandboxPermissionGrant("vault.read", "notes/*"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(Engine(rt), spec));

        Assert.Empty(rt.Results);
    }

    [Fact]
    public async Task Egress_to_host_outside_allowlist_is_withheld()
    {
        var rt = new FakeRuntime([_http], [("bazaar.http_fetch", "evil.example")]);
        var spec = Spec(new SandboxPermissionGrant("net.egress", "*"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(Engine(rt), spec));

        Assert.Empty(rt.Results);
    }

    [Fact]
    public async Task Limits_and_entry_point_are_passed_to_the_runtime()
    {
        var rt = new FakeRuntime([], []);
        await DrainAsync(Engine(rt, new WasmEngineOptions { Fuel = 1234, EntryPoint = "main" }), Spec());

        Assert.Equal(new WasmLimits(64L * 1024 * 1024, 1234), rt.Limits);
        Assert.Equal("main", rt.Entry);
    }

    [Fact]
    public async Task Missing_module_fails_closed()
    {
        var rt = new FakeRuntime([], []);
        var e = new WasmSandboxEngine(rt, new FakeModuleSource(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(e, Spec()));

        Assert.False(rt.Instantiated);
    }

    [Fact]
    public async Task Fuel_exhaustion_is_an_error()
    {
        var e = new WasmSandboxEngine(new ExhaustedRuntime(), new FakeModuleSource([0]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(e, Spec()));
    }

    [Fact]
    public async Task End_to_end_granted_calls_are_clean()
    {
        var rt = new FakeRuntime([_vault], [("bazaar.vault_read", "notes/a")]);
        var result = await Runner(rt).RunAsync(Spec(new SandboxPermissionGrant("vault.read", "notes/*")), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.Clean, result.Verdict);
        Assert.Equal(["synthetic page"], rt.Results);
    }

    [Fact]
    public async Task End_to_end_ungranted_import_is_undeclared_access()
    {
        var rt = new FakeRuntime([_vault, _http], [("bazaar.vault_read", "notes/a")]);
        var result = await Runner(rt).RunAsync(Spec(new SandboxPermissionGrant("vault.read", "*")), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.UndeclaredAccess, result.Verdict);
        var v = Assert.Single(result.Violations);
        Assert.Equal(SandboxActionKind.PermissionCheck, v.Action.Kind);
        Assert.False(rt.Instantiated);
    }

    [Fact]
    public async Task End_to_end_denied_target_is_never_serviced()
    {
        var rt = new FakeRuntime([_vault], [("bazaar.vault_read", "other/x")]);
        var result = await Runner(rt).RunAsync(Spec(new SandboxPermissionGrant("vault.read", "notes/*")), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.UndeclaredAccess, result.Verdict);
        Assert.Empty(rt.Results);
    }

    private static SandboxRunner Runner(FakeRuntime rt) => new(
        [new FakeProvider(SandboxOs.Linux)],
        [Engine(rt)],
        new FakeCompanyFactory(),
        hostOs: SandboxOs.Linux);

    private sealed class ExhaustedRuntime : IWasmRuntime
    {
        public IReadOnlyList<WasmImport> GetDeclaredImports(ReadOnlyMemory<byte> moduleBytes) => [];

        public Task<WasmRunOutcome> RunAsync(
            ReadOnlyMemory<byte> moduleBytes, IReadOnlyCollection<WasmImport> linkedImports, IWasmHostImports host, WasmLimits limits, string entryPoint, CancellationToken ct) =>
            Task.FromResult(new WasmRunOutcome(WasmRunStatus.FuelExhausted));
    }
}
