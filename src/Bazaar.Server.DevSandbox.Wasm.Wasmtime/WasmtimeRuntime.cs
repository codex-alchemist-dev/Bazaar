using System.Globalization;
using System.Text;
using Wasmtime;

namespace Bazaar.Server.DevSandbox.Wasm.Wasmtime;

/// <summary>
/// <see cref="IWasmRuntime"/> backed by Wasmtime (Bytecode Alliance), doc 48 section 2.1. Nothing is linked except the imports
/// the engine passes as linked: no WASI, no default host functions, no unknown-import stubs.
/// </summary>
/// <remarks>
/// <para><b>Guest ABI</b> (all strings are UTF-8, all integers are little-endian WebAssembly values).</para>
/// <list type="bullet">
/// <item>The module exports a linear memory named <c>memory</c> and a function <c>alloc(len: i32) -&gt; i32</c> that returns a pointer
/// to <c>len</c> writable bytes in that memory (the host writes import results there).</item>
/// <item>Every host import has the exact type <c>(ptr: i32, len: i32) -&gt; i64</c>. <c>ptr,len</c> locate the UTF-8 argument in guest
/// memory. The result is packed as <c>(resultPtr &lt;&lt; 32) | resultLen</c> (both unsigned 32-bit); an empty result is
/// <c>0</c>.</item>
/// <item>The entry point is an export with type <c>() -&gt; ()</c> (result is empty) or <c>() -&gt; i64</c> (packed ptr/len of the UTF-8
/// result string, same packing as above).</item>
/// </list>
/// <para>Fail closed: a declared import that is not linked, a non-function import, a wrong import or entry signature, a missing
/// <c>memory</c> or <c>alloc</c>, or a pointer outside guest memory never yields <see cref="WasmRunStatus.Completed"/>. Unlinked
/// imports, non-function imports and signature mismatches throw <see cref="InvalidOperationException"/> before any guest code runs;
/// compile errors, traps and out-of-bounds pointers give <see cref="WasmRunStatus.Trapped"/>; running out of fuel gives
/// <see cref="WasmRunStatus.FuelExhausted"/>.</para>
/// <para>Memory: the Store limiter caps every linear memory at <see cref="WasmLimits.MemoryLimitBytes"/>. A <c>memory.grow</c> beyond
/// the cap fails by returning -1 to the guest (WebAssembly semantics; the cap still holds). A module whose declared initial memory
/// exceeds the cap is refused with <see cref="WasmRunStatus.MemoryLimitExceeded"/>.</para>
/// </remarks>
public sealed class WasmtimeRuntime : IWasmRuntime
{
    private const string _memoryName = "memory";
    private const string _allocName = "alloc";
    private const long _pageSize = 65536;
    private const int _maxStringBytes = 16 * 1024 * 1024;

    /// <inheritdoc />
    public IReadOnlyList<WasmImport> GetDeclaredImports(ReadOnlyMemory<byte> moduleBytes)
    {
        using var engine = new Engine(NewConfig());
        using var module = Module.FromBytes(engine, "module", moduleBytes.Span);
        return module.Imports.Select(i => new WasmImport(i.ModuleName, i.Name)).ToList();
    }

    /// <inheritdoc />
    public Task<WasmRunOutcome> RunAsync(
        ReadOnlyMemory<byte> moduleBytes,
        IReadOnlyCollection<WasmImport> linkedImports,
        IWasmHostImports host,
        WasmLimits limits,
        string entryPoint,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(linkedImports);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentException.ThrowIfNullOrEmpty(entryPoint);
        if (limits.MemoryLimitBytes <= 0 || limits.Fuel <= 0)
        {
            throw new InvalidOperationException("wasm limits must be positive");
        }

        var bytes = moduleBytes.ToArray();
        return Task.Run(() => Run(bytes, linkedImports, host, limits, entryPoint, ct), CancellationToken.None);
    }

    private static Config NewConfig() => new Config()
        .WithFuelConsumption(true)
        .WithEpochInterruption(true)
        .WithWasmThreads(false)
        .WithComponentModel(false);

    private static WasmRunOutcome Run(
        byte[] bytes, IReadOnlyCollection<WasmImport> linkedImports, IWasmHostImports host, WasmLimits limits, string entryPoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var engine = new Engine(NewConfig());

        Module module;
        try
        {
            module = Module.FromBytes(engine, "module", bytes);
        }
        catch (WasmtimeException)
        {
            return new WasmRunOutcome(WasmRunStatus.Trapped);
        }

        using (module)
        {
            // Fail closed: every declared import must be a function the caller listed as linked, with the ABI signature.
            var linked = new HashSet<string>(linkedImports.Select(i => i.Key), StringComparer.Ordinal);
            foreach (var import in module.Imports)
            {
                var key = import.ModuleName + "." + import.Name;
                if (!linked.Contains(key))
                {
                    throw new InvalidOperationException($"module declares import '{key}' that is not linked");
                }

                if (import is not FunctionImport fn
                    || fn.Parameters.Count != 2 || fn.Parameters[0] != ValueKind.Int32 || fn.Parameters[1] != ValueKind.Int32
                    || fn.Results.Count != 1 || fn.Results[0] != ValueKind.Int64)
                {
                    throw new InvalidOperationException($"import '{key}' does not have the ABI signature (i32, i32) -> i64");
                }
            }

            if (module.Exports.OfType<MemoryExport>().Any(m => m.Minimum * _pageSize > limits.MemoryLimitBytes))
            {
                return new WasmRunOutcome(WasmRunStatus.MemoryLimitExceeded);
            }

            using var store = new Store(engine);
            store.Fuel = (ulong)limits.Fuel;
            store.SetLimits(limits.MemoryLimitBytes, null, 1, 1, 1);
            store.SetEpochDeadline(1);

            using var linker = new Linker(engine);
            foreach (var import in module.Imports)
            {
                var key = import.ModuleName + "." + import.Name;
                linker.DefineFunction(
                    import.ModuleName,
                    import.Name,
                    (Caller caller, ReadOnlySpan<ValueBox> args, Span<ValueBox> results) =>
                    {
                        results[0] = HostCall(caller, key, args[0].AsInt32(), args[1].AsInt32(), host, ct);
                    },
                    [ValueKind.Int32, ValueKind.Int32],
                    [ValueKind.Int64]);
            }

            using var registration = ct.Register(engine.IncrementEpoch);
            try
            {
                var instance = linker.Instantiate(store, module);
                var entry = instance.GetFunction(entryPoint)
                    ?? throw new InvalidOperationException($"entry point '{entryPoint}' is not exported");
                if (entry.Parameters.Count != 0 || entry.Results.Count > 1 || (entry.Results.Count == 1 && entry.Results[0] != ValueKind.Int64))
                {
                    throw new InvalidOperationException("entry point must have type () -> () or () -> i64");
                }

                var returned = entry.Invoke();
                ct.ThrowIfCancellationRequested();
                if (entry.Results.Count == 0)
                {
                    return new WasmRunOutcome(WasmRunStatus.Completed);
                }

                var memory = instance.GetMemory(_memoryName) ?? throw new InvalidOperationException("module exports no 'memory'");
                return new WasmRunOutcome(WasmRunStatus.Completed, ReadPacked(memory, Convert.ToInt64(returned, CultureInfo.InvariantCulture)));
            }
            catch (TrapException trap)
            {
                ct.ThrowIfCancellationRequested();
                return new WasmRunOutcome(trap.Type == TrapCode.OutOfFuel ? WasmRunStatus.FuelExhausted : WasmRunStatus.Trapped);
            }
            catch (WasmtimeException)
            {
                ct.ThrowIfCancellationRequested();

                // A host import failed (guest pointer out of bounds, a refused call) or instantiation was refused: never Completed.
                return new WasmRunOutcome(WasmRunStatus.Trapped);
            }
        }
    }

    private static long HostCall(Caller caller, string key, int ptr, int len, IWasmHostImports host, CancellationToken ct)
    {
        var memory = caller.GetMemory(_memoryName) ?? throw new InvalidOperationException("module exports no 'memory'");
        var argument = ReadString(memory, ptr, len);
        var result = host.CallAsync(key, argument, ct).AsTask().GetAwaiter().GetResult() ?? string.Empty;
        var data = Encoding.UTF8.GetBytes(result);
        if (data.Length == 0)
        {
            return 0;
        }

        var alloc = caller.GetFunction(_allocName) ?? throw new InvalidOperationException("module exports no 'alloc'");
        if (alloc.Parameters.Count != 1 || alloc.Parameters[0] != ValueKind.Int32 || alloc.Results.Count != 1 || alloc.Results[0] != ValueKind.Int32)
        {
            throw new InvalidOperationException("alloc must have type (i32) -> i32");
        }

        var outPtr = Convert.ToInt32(alloc.Invoke(data.Length), CultureInfo.InvariantCulture);

        // Re-fetch the memory: alloc may have grown (and so moved) it.
        memory = caller.GetMemory(_memoryName) ?? throw new InvalidOperationException("module exports no 'memory'");
        CheckBounds(memory, outPtr, data.Length);
        data.CopyTo(memory.GetSpan((long)(uint)outPtr, data.Length));
        return ((long)(uint)outPtr << 32) | (uint)data.Length;
    }

    private static string ReadPacked(Memory memory, long packed) =>
        packed == 0 ? string.Empty : ReadString(memory, (int)(packed >>> 32), (int)(packed & 0xFFFFFFFFL));

    private static string ReadString(Memory memory, int ptr, int len)
    {
        CheckBounds(memory, ptr, len);
        return Encoding.UTF8.GetString(memory.GetSpan((long)(uint)ptr, len));
    }

    private static void CheckBounds(Memory memory, int ptr, int len)
    {
        if (len < 0 || len > _maxStringBytes || (uint)ptr + (long)len > memory.GetLength())
        {
            throw new InvalidOperationException("guest pointer outside linear memory");
        }
    }
}
