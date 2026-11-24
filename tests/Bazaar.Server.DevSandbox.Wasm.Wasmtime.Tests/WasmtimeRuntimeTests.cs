using System.Text;
using Wasmtime;

namespace Bazaar.Server.DevSandbox.Wasm.Wasmtime.Tests;

internal sealed class FakeProcess : ISandboxProcess
{
    public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeLinuxProvider : ISandboxConfinementProvider
{
    public SandboxOs Os => SandboxOs.Linux;

    public bool Tested => true;

    public Task<ISandboxProcess> StartAsync(SandboxRunSpec spec, CancellationToken ct = default) =>
        Task.FromResult<ISandboxProcess>(new FakeProcess());
}

internal sealed class FakeCompanyFactory : ISandboxCompanyFactory
{
    public SandboxTestCompany Create(SandboxRunSpec spec) => new(
        "sandbox-run-1",
        "sandbox_run_1",
        "sandbox_run_1",
        new Dictionary<string, string> { ["notes/a"] = "synthetic page" },
        ["CANARY-XYZ"]);
}

internal sealed class BytesSource(byte[] bytes) : IWasmModuleSource
{
    public ValueTask<ReadOnlyMemory<byte>?> GetModuleAsync(string moduleId, string moduleVersion, CancellationToken ct) =>
        ValueTask.FromResult<ReadOnlyMemory<byte>?>(new ReadOnlyMemory<byte>(bytes));
}

internal sealed class RecordingHost(string reply) : IWasmHostImports
{
    public List<(string Name, string Argument)> Calls { get; } = [];

    public ValueTask<string> CallAsync(string importName, string argument, CancellationToken ct)
    {
        Calls.Add((importName, argument));
        return ValueTask.FromResult(reply);
    }
}

public class WasmtimeRuntimeTests
{
    private static readonly WasmImport _vault = new("bazaar", "vault_read");
    private static readonly WasmLimits _limits = new(1024 * 1024, 10_000_000);

    // Shared ABI preamble (see WasmtimeRuntime): exported memory, a bump-pointer alloc, data "notes/a" at 0, scratch from 1024.
    private const string _preamble = """
        (memory (export "memory") 1)
        (global $bump (mut i32) (i32.const 1024))
        (func (export "alloc") (param $n i32) (result i32) (local $p i32)
          global.get $bump
          local.set $p
          global.get $bump
          local.get $n
          i32.add
          global.set $bump
          local.get $p)
        (data (i32.const 0) "notes/a")
        """;

    private static readonly Lazy<string?> _nativeProblem = new(() =>
    {
        try
        {
            using var engine = new Engine();
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return ex.Message;
        }
    });

    private static void RequireNative() => Assert.SkipUnless(_nativeProblem.Value is null, "Wasmtime native library cannot load: " + _nativeProblem.Value);

    private static byte[] Wat(string text)
    {
        using var engine = new Engine();
        return Module.ConvertText(text);
    }

    private static string ModuleWithRun(string body, string imports = "") =>
        $"(module {imports} {_preamble} (func (export \"run\") (result i64) {body}))";

    private static string ModuleLoop() => "(module (func (export \"run\") (loop $l (br $l))))";

    private static async Task<WasmRunOutcome> RunAsync(string wat, RecordingHost host, IReadOnlyCollection<WasmImport>? linked = null, WasmLimits? limits = null)
    {
        var rt = new WasmtimeRuntime();
        return await rt.RunAsync(Wat(wat), linked ?? [], host, limits ?? _limits, "run", TestContext.Current.CancellationToken);
    }

    private const string _vaultImport = "(import \"bazaar\" \"vault_read\" (func $vr (param i32 i32) (result i64)))";

    [Fact]
    public async Task Granted_import_is_called_and_result_is_passed_back()
    {
        RequireNative();
        var host = new RecordingHost("hello from host");

        // run() calls vault_read("notes/a") and returns the packed (ptr,len) it received, so Result is the host's reply.
        var outcome = await RunAsync(ModuleWithRun("(call $vr (i32.const 0) (i32.const 7))", _vaultImport), host, [_vault]);

        Assert.Equal(WasmRunStatus.Completed, outcome.Status);
        Assert.Equal("hello from host", outcome.Result);
        Assert.Equal([("bazaar.vault_read", "notes/a")], host.Calls);
    }

    [Fact]
    public async Task Unlinked_import_makes_instantiation_fail_and_nothing_runs()
    {
        RequireNative();
        var host = new RecordingHost("never");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(ModuleWithRun("(call $vr (i32.const 0) (i32.const 7))", _vaultImport), host, linked: []));

        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Import_with_a_different_signature_is_refused()
    {
        RequireNative();
        var wat = ModuleWithRun("(i64.const 0)", "(import \"bazaar\" \"vault_read\" (func (param i32) (result i32)))");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(wat, new RecordingHost(""), [_vault]));
    }

    [Fact]
    public async Task Non_function_import_is_refused()
    {
        RequireNative();
        var wat = "(module (import \"bazaar\" \"vault_read\" (memory 1)) (func (export \"run\")))";

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(wat, new RecordingHost(""), [_vault]));
    }

    [Fact]
    public void Declared_imports_are_read_from_the_compiled_module()
    {
        RequireNative();
        var wat = ModuleWithRun("(i64.const 0)", _vaultImport + "(import \"env\" \"system\" (func (param i32 i32) (result i64)))");

        var imports = new WasmtimeRuntime().GetDeclaredImports(Wat(wat));

        Assert.Equal(["bazaar.vault_read", "env.system"], imports.Select(i => i.Key).ToArray());
    }

    [Fact]
    public async Task Infinite_loop_runs_out_of_fuel()
    {
        RequireNative();
        var outcome = await RunAsync(ModuleLoop(), new RecordingHost(""), limits: new WasmLimits(1024 * 1024, 100_000));

        Assert.Equal(WasmRunStatus.FuelExhausted, outcome.Status);
        Assert.NotEqual(WasmRunStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task Memory_grow_beyond_the_cap_fails()
    {
        RequireNative();

        // Cap is 1 MiB (16 pages); the module starts with 1 page and tries to grow by 1000 pages. It reports what it saw.
        var wat = $"""
            (module {_preamble.Replace("(data (i32.const 0) \"notes/a\")", "(data (i32.const 0) \"grow-denied\") (data (i32.const 16) \"grow-ok\")")}
              (func (export "run") (result i64)
                (if (result i64) (i32.eq (memory.grow (i32.const 1000)) (i32.const -1))
                  (then (i64.const 11))
                  (else (i64.or (i64.shl (i64.const 16) (i64.const 32)) (i64.const 7))))))
            """;

        var outcome = await RunAsync(wat, new RecordingHost(""));

        Assert.Equal(WasmRunStatus.Completed, outcome.Status);
        Assert.Equal("grow-denied", outcome.Result);
    }

    [Fact]
    public async Task Memory_grow_within_the_cap_is_allowed()
    {
        RequireNative();
        var wat = $"""
            (module {_preamble.Replace("(data (i32.const 0) \"notes/a\")", "(data (i32.const 0) \"grow-denied\") (data (i32.const 16) \"grow-ok\")")}
              (func (export "run") (result i64)
                (if (result i64) (i32.eq (memory.grow (i32.const 2)) (i32.const -1))
                  (then (i64.const 11))
                  (else (i64.or (i64.shl (i64.const 16) (i64.const 32)) (i64.const 7))))))
            """;

        var outcome = await RunAsync(wat, new RecordingHost(""));

        Assert.Equal("grow-ok", outcome.Result);
    }

    [Fact]
    public async Task Initial_memory_above_the_cap_is_refused()
    {
        RequireNative();
        var wat = "(module (memory (export \"memory\") 100) (func (export \"run\")))";

        var outcome = await RunAsync(wat, new RecordingHost(""));

        Assert.Equal(WasmRunStatus.MemoryLimitExceeded, outcome.Status);
    }

    [Fact]
    public async Task Trap_is_reported_as_trapped()
    {
        RequireNative();
        var outcome = await RunAsync("(module (func (export \"run\") unreachable))", new RecordingHost(""));

        Assert.Equal(WasmRunStatus.Trapped, outcome.Status);
    }

    [Fact]
    public async Task Invalid_module_is_never_completed()
    {
        RequireNative();
        var rt = new WasmtimeRuntime();
        var outcome = await rt.RunAsync(Encoding.ASCII.GetBytes("not wasm"), [], new RecordingHost(""), _limits, "run", TestContext.Current.CancellationToken);

        Assert.NotEqual(WasmRunStatus.Completed, outcome.Status);
        Assert.Throws<WasmtimeException>(() => rt.GetDeclaredImports(Encoding.ASCII.GetBytes("not wasm")));
    }

    [Fact]
    public async Task Missing_entry_point_fails_closed()
    {
        RequireNative();
        var rt = new WasmtimeRuntime();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rt.RunAsync(Wat("(module (func (export \"other\")))"), [], new RecordingHost(""), _limits, "run", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Guest_pointer_outside_memory_traps()
    {
        RequireNative();
        var outcome = await RunAsync(
            ModuleWithRun("(call $vr (i32.const 65530) (i32.const 100))", _vaultImport), new RecordingHost("x"), [_vault]);

        Assert.Equal(WasmRunStatus.Trapped, outcome.Status);
    }

    private static SandboxRunSpec Spec(params SandboxPermissionGrant[] grants) => new(
        "run-1", "mod.example", "1.0.0", SandboxEngineKind.Wasm, grants, ["svc.example.test"], 42UL, 30, 64);

    private static SandboxRunner Runner(string wat) => new(
        [new FakeLinuxProvider()],
        [new WasmSandboxEngine(new WasmtimeRuntime(), new BytesSource(Wat(wat)))],
        new FakeCompanyFactory(),
        hostOs: SandboxOs.Linux);

    [Fact]
    public async Task End_to_end_granted_import_is_clean()
    {
        RequireNative();
        var runner = Runner(ModuleWithRun("(call $vr (i32.const 0) (i32.const 7))", _vaultImport));

        var result = await runner.RunAsync(Spec(new SandboxPermissionGrant("vault.read", "notes/*")), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.Clean, result.Verdict);
    }

    [Fact]
    public async Task End_to_end_ungranted_import_is_undeclared_access()
    {
        RequireNative();
        var wat = ModuleWithRun(
            "(call $vr (i32.const 0) (i32.const 7))",
            _vaultImport + "(import \"bazaar\" \"http_fetch\" (func (param i32 i32) (result i64)))");
        var runner = Runner(wat);

        var result = await runner.RunAsync(Spec(new SandboxPermissionGrant("vault.read", "notes/*")), TestContext.Current.CancellationToken);

        Assert.Equal(SandboxVerdict.UndeclaredAccess, result.Verdict);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("net.egress", violation.Action.Class);
    }

    [Fact]
    public async Task End_to_end_infinite_loop_is_not_clean()
    {
        RequireNative();
        var runner = new SandboxRunner(
            [new FakeLinuxProvider()],
            [new WasmSandboxEngine(new WasmtimeRuntime(), new BytesSource(Wat(ModuleLoop())), new WasmEngineOptions { Fuel = 100_000 })],
            new FakeCompanyFactory(),
            hostOs: SandboxOs.Linux);

        var result = await runner.RunAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.NotEqual(SandboxVerdict.Clean, result.Verdict);
    }
}
