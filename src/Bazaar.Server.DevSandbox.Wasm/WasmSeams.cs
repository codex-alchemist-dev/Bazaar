namespace Bazaar.Server.DevSandbox.Wasm;

/// <summary>One import a WebAssembly module declares, as a (module, name) pair.</summary>
/// <param name="Module">The import's module namespace (for example <c>bazaar</c>).</param>
/// <param name="Name">The import's name within the module (for example <c>vault_read</c>).</param>
public sealed record WasmImport(string Module, string Name)
{
    /// <summary>The catalogue key, <c>module.name</c> (for example <c>bazaar.vault_read</c>).</summary>
    public string Key => Module + "." + Name;
}

/// <summary>Resource limits the runtime must enforce for one run.</summary>
/// <param name="MemoryLimitBytes">Maximum linear memory the instance may reach.</param>
/// <param name="Fuel">Instruction (fuel) budget. When it is used up the run stops.</param>
public sealed record WasmLimits(long MemoryLimitBytes, long Fuel);

/// <summary>How an instance ended.</summary>
public enum WasmRunStatus
{
    /// <summary>The entry point returned normally.</summary>
    Completed,

    /// <summary>The fuel budget ran out.</summary>
    FuelExhausted,

    /// <summary>The memory ceiling was hit.</summary>
    MemoryLimitExceeded,

    /// <summary>The module trapped.</summary>
    Trapped,
}

/// <summary>The result of running an entry point.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Result">The entry point's string result (empty when it has none).</param>
public sealed record WasmRunOutcome(WasmRunStatus Status, string Result = "");

/// <summary>The host side of the module's imports. Each import call from the module arrives here.</summary>
public interface IWasmHostImports
{
    /// <summary>Services one import call.</summary>
    /// <param name="importName">The catalogue key of the import, for example <c>bazaar.vault_read</c>.</param>
    /// <param name="argument">The call's string argument.</param>
    /// <param name="ct">Cancelled when the run is abandoned; the runtime must then stop the instance.</param>
    /// <returns>The result string handed back to the module.</returns>
    ValueTask<string> CallAsync(string importName, string argument, CancellationToken ct);
}

/// <summary>
/// The WebAssembly runtime seam. Choosing a concrete runtime package is an OPEN owner decision (doc 48 section 2.1); the
/// engine depends only on this interface.
/// </summary>
public interface IWasmRuntime
{
    /// <summary>Reads the imports the module declares without instantiating it. Throws when the bytes are not a valid module.</summary>
    /// <param name="moduleBytes">The module bytes.</param>
    IReadOnlyList<WasmImport> GetDeclaredImports(ReadOnlyMemory<byte> moduleBytes);

    /// <summary>
    /// Instantiates the module and runs <paramref name="entryPoint"/>. Contract: the runtime links exactly
    /// <paramref name="linkedImports"/>, refuses to instantiate when the module declares any other import, enforces
    /// <paramref name="limits"/>, and sends every import call through <paramref name="host"/> and nowhere else.
    /// </summary>
    /// <param name="moduleBytes">The module bytes.</param>
    /// <param name="linkedImports">The only imports that may be linked.</param>
    /// <param name="host">The host side every import call goes to.</param>
    /// <param name="limits">Memory and fuel limits.</param>
    /// <param name="entryPoint">The exported function to run.</param>
    /// <param name="ct">Cancelled when the run is abandoned.</param>
    Task<WasmRunOutcome> RunAsync(
        ReadOnlyMemory<byte> moduleBytes,
        IReadOnlyCollection<WasmImport> linkedImports,
        IWasmHostImports host,
        WasmLimits limits,
        string entryPoint,
        CancellationToken ct);
}

/// <summary>Supplies the bytes of the module under test.</summary>
public interface IWasmModuleSource
{
    /// <summary>Returns the module bytes for the id and version, or null when there is none.</summary>
    /// <param name="moduleId">The module id.</param>
    /// <param name="moduleVersion">The module version.</param>
    /// <param name="ct">Cancellation.</param>
    ValueTask<ReadOnlyMemory<byte>?> GetModuleAsync(string moduleId, string moduleVersion, CancellationToken ct);
}
