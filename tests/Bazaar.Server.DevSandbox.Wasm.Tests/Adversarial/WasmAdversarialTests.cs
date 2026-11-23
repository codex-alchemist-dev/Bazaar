namespace Bazaar.Server.DevSandbox.Wasm.Tests.Adversarial;

public class WasmAdversarialTests
{
    private static SandboxRunSpec Spec(params SandboxPermissionGrant[] grants) => new(
        "run-1", "mod.example", "1.0.0", SandboxEngineKind.Wasm, grants, ["svc.example.test"], 42UL, 30, 64);

    private static async Task<List<SandboxAction>> DrainAsync(FakeRuntime rt, SandboxRunSpec spec, WasmEngineOptions? options = null)
    {
        var engine = new WasmSandboxEngine(rt, new FakeModuleSource([0, 1]), options);
        var list = new List<SandboxAction>();
        await foreach (var a in engine.RunAsync(spec, new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
        {
            list.Add(a);
        }

        return list;
    }

    [Theory]
    [InlineData("wasi_snapshot_preview1", "fd_write")]
    [InlineData("wasi_snapshot_preview1", "path_open")]
    [InlineData("wasi_snapshot_preview1", "sock_accept")]
    [InlineData("env", "system")]
    [InlineData("env", "memory")]
    [InlineData("bazaar", "vault_write")]
    [InlineData("bazaar", "VAULT_READ")]
    [InlineData("Bazaar", "vault_read")]
    [InlineData("bazaar", "vault_read ")]
    [InlineData("bazaar.vault", "read")]
    [InlineData("", "")]
    [InlineData("", "bazaar.vault_read")]
    public async Task An_unknown_or_look_alike_import_is_reported_and_the_module_is_never_instantiated(string module, string name)
    {
        var rt = new FakeRuntime([new WasmImport(module, name)], []);
        var spec = Spec(new SandboxPermissionGrant("vault.read", "*"), new SandboxPermissionGrant("net.egress", "*"), new SandboxPermissionGrant("fs.read", "*"));

        var actions = await DrainAsync(rt, spec);

        var refused = Assert.Single(actions);
        Assert.Equal(SandboxActionKind.PermissionCheck, refused.Kind);
        Assert.Equal(WasmImportCatalogue.UnknownImportClass, refused.Class);
        Assert.False(rt.Instantiated);
        Assert.Empty(rt.Linked);
    }

    [Fact]
    public async Task One_unknown_import_among_granted_ones_stops_everything()
    {
        var rt = new FakeRuntime([new WasmImport("bazaar", "vault_read"), new WasmImport("env", "evil")], [("bazaar.vault_read", "notes/a")]);

        var actions = await DrainAsync(rt, Spec(new SandboxPermissionGrant("vault.read", "*")));

        Assert.Single(actions);
        Assert.False(rt.Instantiated);
    }

    [Fact]
    public async Task Only_the_declared_and_granted_imports_are_linked()
    {
        var rt = new FakeRuntime([new WasmImport("bazaar", "vault_read")], []);

        await DrainAsync(rt, Spec(new SandboxPermissionGrant("vault.read", "*"), new SandboxPermissionGrant("net.egress", "*")));

        Assert.Equal(["bazaar.vault_read"], rt.Linked.Select(i => i.Key));
    }

    [Fact]
    public async Task Fuel_memory_and_a_positive_budget_are_passed_to_the_runtime()
    {
        var rt = new FakeRuntime([], []);

        await DrainAsync(rt, Spec(), new WasmEngineOptions { Fuel = 12345 });

        Assert.Equal(12345, rt.Limits!.Fuel);
        Assert.Equal(64L * 1024 * 1024, rt.Limits.MemoryLimitBytes);
    }

    [Theory]
    [InlineData(0L, 10)]
    [InlineData(-1L, 10)]
    [InlineData(1L, 0)]
    public async Task Non_positive_limits_refuse_to_run(long fuel, int maxCalls)
    {
        var rt = new FakeRuntime([], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(rt, Spec(), new WasmEngineOptions { Fuel = fuel, MaxHostCalls = maxCalls }));

        Assert.False(rt.Instantiated);
    }

    [Theory]
    [InlineData("/data/mod/../../etc/passwd")]
    [InlineData("/data/mod//x")]
    [InlineData("/data/mod/./x")]
    [InlineData("/data/mod/x\0")]
    [InlineData("/data/mod/x\\..\\y")]
    public async Task A_call_the_policy_would_refuse_is_never_serviced_even_when_the_consumer_keeps_going(string target)
    {
        // The runner keeps enumerating after a denial. A target with a traversal must not be serviced through a looser pre-check.
        var records = FakeCompanyFactory.Company().Records;
        var rt = new FakeRuntime([new WasmImport("bazaar", "file_read")], [("bazaar.file_read", target)]);

        var actions = new List<SandboxAction>();
        var engine = new WasmSandboxEngine(rt, new FakeModuleSource([0, 1]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var a in engine.RunAsync(Spec(new SandboxPermissionGrant("fs.read", "/data/mod/*")), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
            {
                actions.Add(a);
            }
        });

        Assert.Single(actions);
        Assert.Empty(rt.Results);
        Assert.NotEmpty(records);
    }

    [Fact]
    public async Task A_network_call_to_a_non_canonical_host_is_not_serviced()
    {
        var rt = new FakeRuntime([new WasmImport("bazaar", "http_fetch")], [("bazaar.http_fetch", "svc.example.test.\nbody")]);
        var engine = new WasmSandboxEngine(rt, new FakeModuleSource([0, 1]));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var unused in engine.RunAsync(
                Spec(new SandboxPermissionGrant("net.egress", "*")), new FakeProcess(), FakeCompanyFactory.Company(), TestContext.Current.CancellationToken))
            {
                _ = unused;
            }
        });

        Assert.Empty(rt.Results);
    }

    [Fact]
    public async Task An_oversized_import_argument_is_refused()
    {
        var rt = new FakeRuntime([new WasmImport("bazaar", "vault_read")], [("bazaar.vault_read", new string('a', 2 * 1024 * 1024))]);

        await Assert.ThrowsAnyAsync<Exception>(() => DrainAsync(rt, Spec(new SandboxPermissionGrant("vault.read", "*"))));

        Assert.Empty(rt.Results);
    }

    [Fact]
    public async Task Host_calls_beyond_the_limit_are_refused()
    {
        var calls = Enumerable.Range(0, 5).Select(_ => ("bazaar.vault_read", "notes/a")).ToArray();
        var rt = new FakeRuntime([new WasmImport("bazaar", "vault_read")], calls);

        await Assert.ThrowsAnyAsync<Exception>(() => DrainAsync(rt, Spec(new SandboxPermissionGrant("vault.read", "*")), new WasmEngineOptions { MaxHostCalls = 3 }));

        Assert.True(rt.Results.Count <= 3);
    }

    [Fact]
    public void The_catalogue_has_no_wasi_and_no_wildcard_entries()
    {
        Assert.False(WasmImportCatalogue.TryGet("wasi_snapshot_preview1.fd_write", out _));
        Assert.False(WasmImportCatalogue.TryGet("*", out _));
        Assert.False(WasmImportCatalogue.TryGet("bazaar.*", out _));
        Assert.False(WasmImportCatalogue.TryGet("BAZAAR.VAULT_READ", out _));
        Assert.True(WasmImportCatalogue.TryGet("bazaar.vault_read", out var info));
        Assert.Equal("vault.read", info.Class);
    }
}
