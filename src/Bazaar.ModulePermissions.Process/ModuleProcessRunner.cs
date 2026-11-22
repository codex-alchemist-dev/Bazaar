using System.Diagnostics;
using System.Text;

namespace Bazaar.ModulePermissions.Process;

/// <summary>
/// The only way a hosted module starts a program (doc 48 section 1, the <c>process</c> class). The module names a program
/// from the host's <see cref="ModuleProgramCatalog"/>; the permission gate is asked first, so a refused run is audited and an
/// ungranted name tells the module nothing about which programs exist.
/// </summary>
/// <remarks>
/// What a run gets: the catalog's executable and working directory, the module's arguments passed one by one with no shell,
/// an environment built from a short allowlist plus what the catalog sets (never the server's own environment, which holds
/// secrets), bounded input and output, a time limit, and a kill of the whole process tree at that limit or on cancel.
/// What it does not get: confinement by the operating system. The program runs under the server's account and can do what
/// that account can, so the host offers only programs it trusts with the arguments a module may pass. Confined execution is
/// the sandbox provider's job (doc 48 section 2).
/// </remarks>
public sealed class ModuleProcessRunner
{
    // Variables a program needs to start. None carries a credential by convention, and none names the server account's
    // profile (no HOME, USERPROFILE or APPDATA): a module's program has no business in it. Case-insensitive, as on Windows.
    private static readonly HashSet<string> _inherited = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "TEMP", "TMP", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE", "TZ",
    };

    private static readonly TimeSpan _drainWait = TimeSpan.FromSeconds(5);

    private readonly IModulePermissionGate _gate;
    private readonly string _moduleId;
    private readonly ModuleProgramCatalog _catalog;
    private readonly ModuleProcessOptions _options;
    private readonly TimeSpan _timeout;
    private int _running;

    /// <summary>Creates the runner of one module.</summary>
    public ModuleProcessRunner(IModulePermissionGate gate, string moduleId, ModuleProgramCatalog catalog, ModuleProcessOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(catalog);
        _gate = gate;
        _moduleId = moduleId;
        _catalog = catalog;
        _options = options ?? new ModuleProcessOptions();
        _timeout = _options.Timeout ?? ModuleProcessOptions.DefaultTimeout;

        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxArguments);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxArgumentChars);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxCommandChars);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxInputChars);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxOutputChars);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConcurrent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_timeout, ModuleProcessOptions.MaxTimeout);
    }

    /// <summary>True when a variable of the server's environment is passed on to a module's program.</summary>
    public static bool IsInherited(string name) => _inherited.Contains(name);

    /// <summary>
    /// Runs a program to its end and returns what it wrote. Throws <see cref="ModulePermissionDeniedException"/> when the
    /// module holds no <c>process</c> permission for <paramref name="program"/>, and
    /// <see cref="ModuleProcessRefusedException"/> for every other refusal. A cancelled run is killed and throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<ModuleProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);

        // The gate is asked before any other rule can refuse, so every refused run is audited (doc 48 section 1).
        _gate.Demand(_moduleId, new ModuleAccess(ModulePermissionClasses.ProcessSpawn, program));

        var entry = _catalog.Find(program) ?? throw Refuse("The program is not available.");
        CheckArguments(arguments);
        if (standardInput is not null && (standardInput.Length > _options.MaxInputChars || standardInput.Contains('\0', StringComparison.Ordinal)))
        {
            throw Refuse("The input is not accepted.");
        }

        ct.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _running) > _options.MaxConcurrent)
        {
            Interlocked.Decrement(ref _running);
            throw Refuse("Too many programs are running.");
        }

        try
        {
            return await RunCoreAsync(entry, arguments, standardInput, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static ModuleProcessRefusedException Refuse(string reason) => new(reason);

    private static void Kill(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or going. The wait that follows settles it.
        }
    }

    private void CheckArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > _options.MaxArguments)
        {
            throw Refuse("The arguments are not accepted.");
        }

        long total = 0;
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Length > _options.MaxArgumentChars || argument.Contains('\0', StringComparison.Ordinal))
            {
                throw Refuse("The arguments are not accepted.");
            }

            total += argument.Length;
        }

        if (total > _options.MaxCommandChars)
        {
            throw Refuse("The arguments are not accepted.");
        }
    }

    private async Task<ModuleProcessResult> RunCoreAsync(ModuleProgram entry, IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
    {
        var start = new ProcessStartInfo(entry.ExecutablePath)
        {
            WorkingDirectory = entry.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && variable.Value is string value && IsInherited(name))
            {
                start.Environment[name] = value;
            }
        }

        if (entry.Environment is { } extra)
        {
            foreach (var (name, value) in extra)
            {
                start.Environment[name] = value;
            }
        }

        using var process = new System.Diagnostics.Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The reason would describe the host (a missing file, a path); the module gets one fixed sentence.
            throw Refuse("The program could not be started.");
        }

        // Both pipes are read from the start, so a program that writes a lot never blocks on a full pipe.
        var stdout = new Capture(process.StandardOutput, _options.MaxOutputChars);
        var stderr = new Capture(process.StandardError, _options.MaxOutputChars);
        var input = WriteInputAsync(process, standardInput);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                await input.ConfigureAwait(false);
                throw;
            }

            timedOut = true;
        }

        await input.ConfigureAwait(false);
        var (outText, outCut) = await stdout.FinishAsync(_drainWait).ConfigureAwait(false);
        var (errText, errCut) = await stderr.FinishAsync(_drainWait).ConfigureAwait(false);
        return new ModuleProcessResult(timedOut ? -1 : process.ExitCode, outText, errText, timedOut, outCut || errCut);
    }

    private static async Task WriteInputAsync(System.Diagnostics.Process process, string? standardInput)
    {
        // Input is written beside the reads, and the pipe is always closed, so a program that waits for the end of its
        // input gets it and a program that never reads cannot hold the run past the time limit.
        try
        {
            if (!string.IsNullOrEmpty(standardInput))
            {
                await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The program closed its input or has exited. What it did not read is dropped.
        }
    }

    /// <summary>
    /// Drains one pipe, keeping at most a cap of characters. Reading goes on past the cap so the program never blocks. The
    /// wait for the end of the stream is bounded: a process that outlives the program and holds the pipe cannot stall the run.
    /// </summary>
    private sealed class Capture
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();
        private readonly int _cap;
        private readonly Task _pump;
        private bool _cut;

        public Capture(StreamReader reader, int cap)
        {
            _cap = cap;
            _pump = Task.Run(() => PumpAsync(reader));
        }

        public async Task<(string Text, bool Cut)> FinishAsync(TimeSpan wait)
        {
            var ended = true;
            try
            {
                await _pump.WaitAsync(wait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ended = false;
            }

            lock (_gate)
            {
                return (_text.ToString(), _cut || !ended);
            }
        }

        private async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[8192];
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        var room = _cap - _text.Length;
                        if (read > room)
                        {
                            _cut = true;
                        }

                        if (room > 0)
                        {
                            _text.Append(buffer, 0, Math.Min(read, room));
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The pipe went away with the process. What was read stands.
            }
        }
    }
}
