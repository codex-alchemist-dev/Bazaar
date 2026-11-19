using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Bazaar.Server.Portability.Sandbox;

/// <summary>
/// Runs Detect and Parse of an importer in a separate child process (doc 44 section 8.3, work package 10). The server
/// process never executes importer code. Every failure (hang, crash, oversize output, broken limit) ends as a
/// <see cref="ParserSandboxException"/> with the child's whole process tree killed and nothing written to the instance.
/// </summary>
public sealed class ParserSandboxRunner
{
    private const string _hostFileName = "Bazaar.Server.Portability.ParserHost.dll";
    private const int _stderrTailChars = 2048;
    private const int _stderrMessageChars = 512;
    private const int _sigkillExitCode = 137;

    /// <summary>Longest source name, in UTF-8 bytes, that is sent to the child.</summary>
    public const int MaxSourceNameBytes = 1024;
    private static readonly TimeSpan _exitGrace = TimeSpan.FromSeconds(5);
    private static readonly string[] _inheritedEnvironment =
        ["PATH", "DOTNET_ROOT", "SystemRoot", "windir", "TEMP", "TMP", "TMPDIR"];

    private readonly ParserSandboxOptions _options;
    private readonly IParserConfinement _confinement;
    private volatile ParserRunInfo? _lastRun;

    public ParserSandboxRunner(
        ParserSandboxOptions options,
        IParserConfinement? confinement = null,
        IParserSignalSink? signals = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _confinement = confinement ?? ParserConfinements.CreateDefault();
        CrashTracker = new ParserCrashTracker(options.CrashLoopThreshold, options.CrashLoopWindow, signals, time, options.SourceFailureCap);
    }

    /// <summary>The crash-loop circuit. Operators reset it after fixing the cause.</summary>
    public ParserCrashTracker CrashTracker { get; }

    /// <summary>What the confinement provider applied to the most recent child, or null before the first job.</summary>
    public ParserRunInfo? LastRun => _lastRun;

    public async Task<DetectResult> DetectAsync(string importerId, ImportSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        CheckSourceName(source.Name);
        var request = ParserRequest.For(ParserOps.Detect, importerId, source.Name, source.Content.LongLength, _options.Limits);
        var messages = await RunAsync(request, source.Content, importerId, SourceKeyOf(source), ct).ConfigureAwait(false);
        var detect = messages.FirstOrDefault(m => m.Type == ParserMessageTypes.DetectResult)
            ?? throw new ParserSandboxException(ParserFailureKind.ProtocolViolation, "the parser host sent no detect result");
        return new DetectResult(detect.Confidence, detect.Reason ?? string.Empty);
    }

    public async Task<IReadOnlyList<SourceRecord>> ParseAsync(string importerId, ImportSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        CheckSourceName(source.Name);
        var request = ParserRequest.For(ParserOps.Parse, importerId, source.Name, source.Content.LongLength, _options.Limits);
        var messages = await RunAsync(request, source.Content, importerId, SourceKeyOf(source), ct).ConfigureAwait(false);
        // ValidateMessage guarantees every record frame carries a complete record.
        return [.. messages.Where(m => m.Type == ParserMessageTypes.Record).Select(m => m.Record ?? throw new ParserSandboxException(ParserFailureKind.ProtocolViolation, "record frame without a record"))];
    }

    /// <summary>Asks the child which importers it can run. Used to build <see cref="SandboxedImporter"/> wrappers.</summary>
    public async Task<IReadOnlyList<ParserImporterInfo>> ListImportersAsync(CancellationToken ct = default)
    {
        var request = ParserRequest.For(ParserOps.List, string.Empty, string.Empty, 0, _options.Limits);
        // A failed list is the host's fault, not a source's, so each call is its own source for the circuit.
        var messages = await RunAsync(request, [], "*", Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        return messages.FirstOrDefault(m => m.Type == ParserMessageTypes.Importers)?.Importers ?? [];
    }

    public DetectResult Detect(string importerId, ImportSource source) =>
        Task.Run(() => DetectAsync(importerId, source)).GetAwaiter().GetResult();

    public IReadOnlyList<SourceRecord> Parse(string importerId, ImportSource source) =>
        Task.Run(() => ParseAsync(importerId, source)).GetAwaiter().GetResult();

    public IReadOnlyList<ParserImporterInfo> ListImporters() =>
        Task.Run(() => ListImportersAsync()).GetAwaiter().GetResult();

    /// <summary>
    /// Importer wrappers for every importer the child offers, ready for <c>new ImportPipeline(...)</c>. The pipeline
    /// then runs unchanged, with Detect and Parse crossing into the sandbox.
    /// </summary>
    public IReadOnlyList<IPortabilityImporter> CreateImporters() =>
        [.. ListImporters().Select(i => new SandboxedImporter(this, i.Id, i.Version))];

    /// <summary>
    /// Identifies a source for the crash circuit: a hash of its name and the hash of its content. Computed here, so a
    /// caller cannot pick a key that spreads or hides failures.
    /// </summary>
    private static string SourceKeyOf(ImportSource source)
    {
        var contentHash = System.Security.Cryptography.SHA256.HashData(source.Content);
        return SourceKeys.For(source.Name, Convert.ToHexString(contentHash));
    }

    private async Task<List<ParserMessage>> RunAsync(ParserRequest request, byte[] content, string trackerKey, string sourceKey, CancellationToken ct)
    {
        CrashTracker.EnsureClosed(trackerKey, sourceKey);
        try
        {
            var messages = await RunChildAsync(request, content, ct).ConfigureAwait(false);
            CrashTracker.RecordSuccess(trackerKey, sourceKey);
            return messages;
        }
        catch (ParserSandboxException ex) when (ex.CountsAgainstSource)
        {
            CrashTracker.RecordFailure(trackerKey, sourceKey, $"{ex.Kind}: {ex.Message}", abnormal: ex.CountsAsCrash);
            throw;
        }
    }

    private async Task<List<ParserMessage>> RunChildAsync(ParserRequest request, byte[] content, CancellationToken ct)
    {
        var limits = new ConfinementLimits(_options.MaxMemoryBytes, _options.MaxCpuSeconds);
        var plan = _confinement.Prepare(limits, BuildCommand());
        var applied = new List<string>(plan.Applied) { "wall-clock-timeout", "managed-heap-limit", "output-cap", "scrubbed-environment", "empty-working-directory" };
        _lastRun = new ParserRunInfo(_confinement.Name, applied, plan.NotApplied);
        if (_options.RequireFullConfinement && plan.NotApplied.Count > 0)
        {
            throw new ParserSandboxException(
                ParserFailureKind.ConfinementUnavailable,
                $"confinement provider '{_confinement.Name}' could not apply: {string.Join(", ", plan.NotApplied)}");
        }

        var scratch = Directory.CreateTempSubdirectory("bazaar-parser-");
        Process? process = null;
        IProcessGuard? guard = null;
        try
        {
            var info = new ProcessStartInfo(plan.Command.FileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = scratch.FullName,
            };
            foreach (var arg in plan.Command.Arguments) info.ArgumentList.Add(arg);
            ScrubEnvironment(info);

            try
            {
                process = Process.Start(info) ?? throw new ParserSandboxException(ParserFailureKind.HostUnavailable, "the parser host did not start");
            }
            catch (Win32Exception ex)
            {
                throw new ParserSandboxException(ParserFailureKind.HostUnavailable, "the parser host could not be started: " + ex.Message, ex);
            }

            try
            {
                guard = plan.AttachAfterStart?.Invoke(process) ?? new ProcessTreeGuard(process);
            }
            catch (Exception ex)
            {
                new ProcessTreeGuard(process).KillTree();
                throw new ParserSandboxException(ParserFailureKind.ConfinementUnavailable, "the confinement provider could not attach to the child: " + ex.Message, ex);
            }

            return await Supervise(process, guard, request, content, ct).ConfigureAwait(false);
        }
        finally
        {
            guard?.KillTree();
            guard?.Dispose();
            process?.Dispose();
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A leftover scratch directory is empty and harmless; the next run uses a new one.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    private async Task<List<ParserMessage>> Supervise(Process process, IProcessGuard guard, ParserRequest request, byte[] content, CancellationToken ct)
    {
        var timeoutReason = 0; // 0 none, 1 record, 2 job, 3 cancelled
        // A descendant that left the child's process group or job can outlive the kill and keep the pipes open, so no
        // wait below may depend on the pipes closing. Once a kill is sent, the reads get a short grace and are then
        // abandoned; disposing the process closes the server's ends.
        using var abandonCts = new CancellationTokenSource();
        void Kill(int reason)
        {
            Interlocked.CompareExchange(ref timeoutReason, reason, 0);
            guard.KillTree();
            try
            {
                abandonCts.CancelAfter(_exitGrace);
            }
            catch (ObjectDisposedException)
            {
                // The job already ended.
            }
        }

        using var jobCts = new CancellationTokenSource(_options.JobTimeout);
        using var jobReg = jobCts.Token.Register(() => Kill(2));
        using var callerReg = ct.Register(() => Kill(3));

        var stderrTail = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[512];
                int n;
                while ((n = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    lock (stderrTail)
                    {
                        stderrTail.Append(buffer, 0, n);
                        if (stderrTail.Length > _stderrTailChars) stderrTail.Remove(0, stderrTail.Length - _stderrTailChars);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The pipe closed with the process.
            }
        }, CancellationToken.None);

        var writeTask = Task.Run(() => WriteInput(process, request, content), CancellationToken.None);

        var messages = new List<ParserMessage>();
        var maxFrame = ParserFrames.MaxFrameBytes(_options.Limits.MaxRecordBytes);
        long outputBytes = 0;
        ParserMessage? terminal = null;
        Exception? readFailure = null;
        int? naturalExitCode = null;
        var stdout = process.StandardOutput.BaseStream;

        try
        {
            while (true)
            {
                // Parse returns a materialized list, so the first frame only arrives after the whole parse. That wait is
                // bounded by the job timeout alone; the frame-gap timeout applies between the frames that follow.
                using var gapCts = new CancellationTokenSource(messages.Count == 0 ? Timeout.InfiniteTimeSpan : _options.FrameGapTimeout);
                using var gapReg = gapCts.Token.Register(() => Kill(1));
                byte[]? body;
                try
                {
                    // WaitAsync as well as the token: not every pipe stream honours cancellation of a pending read.
                    body = await ParserFrames.ReadFrameAsync(stdout, maxFrame, abandonCts.Token).WaitAsync(abandonCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or FrameTooLargeException or IOException or ObjectDisposedException or OperationCanceledException)
                {
                    readFailure = ex;
                    break;
                }

                if (body is null)
                {
                    // End of output: note how the child ended on its own, before any kill of ours can change the exit code.
                    await WaitBoundedAsync(process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
                    if (process.HasExited) naturalExitCode = process.ExitCode;
                    break;
                }
                outputBytes += body.Length + 4L;
                if (outputBytes > _options.MaxOutputBytes)
                {
                    guard.KillTree();
                    throw new ParserSandboxException(ParserFailureKind.OutputTooLarge, $"the parser wrote more than {_options.MaxOutputBytes} bytes");
                }

                ParserMessage message;
                try
                {
                    message = System.Text.Json.JsonSerializer.Deserialize(body, ParserJsonContext.Default.ParserMessage)
                        ?? throw new System.Text.Json.JsonException("empty message");
                    ValidateMessage(request.Op, message);
                }
                catch (ParserSandboxException)
                {
                    guard.KillTree();
                    throw;
                }
                catch (Exception ex)
                {
                    // Any failure to decode a child frame is a protocol violation, never a server error.
                    guard.KillTree();
                    throw new ParserSandboxException(ParserFailureKind.ProtocolViolation, "the parser sent a frame that is not valid: " + Untrusted(ex.Message, 200), ex);
                }

                if (message.Type is ParserMessageTypes.Done or ParserMessageTypes.Error)
                {
                    terminal = message;
                    break;
                }

                if (message.Type == ParserMessageTypes.Record && message.Record is { } record
                    && ParserFrames.RecordBytes(record) > _options.Limits.MaxRecordBytes)
                {
                    guard.KillTree();
                    throw new ParserSandboxException(ParserFailureKind.LimitExceeded, $"a record is over the limit of {_options.Limits.MaxRecordBytes} bytes (all fields counted)");
                }

                messages.Add(message);
            }
        }
        finally
        {
            // Whatever ended the loop, stop the child before the checks below, so the writer and the pipes unblock.
            if (terminal is null) Kill(0);
        }

        if (terminal is not null)
        {
            // The job is decided. Give the child a moment to exit on its own, then end the tree.
            using var graceCts = new CancellationTokenSource(_exitGrace);
            try
            {
                await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(0);
            }
        }
        else
        {
            await WaitBoundedAsync(process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
        }

        // Bounded for the same reason as the frame read: an escaped descendant may hold stdin or stderr open.
        await WaitBoundedAsync(Task.WhenAll(writeTask, stderrTask)).ConfigureAwait(false);

        // A finished job (done or error frame) stands even if a timer fired while the child was exiting.
        if (terminal is null)
        {
            switch (Volatile.Read(ref timeoutReason))
            {
                case 1:
                    throw new ParserSandboxException(ParserFailureKind.Timeout, $"the parser sent no frame for {_options.FrameGapTimeout}; its process tree was killed");
                case 2:
                    // Not counted toward the crash loop: a legitimately slow parse of a large source looks the same.
                    throw new ParserSandboxException(ParserFailureKind.JobTimeout, $"the parser job ran longer than {_options.JobTimeout}; its process tree was killed");
                case 3:
                    throw new OperationCanceledException(ct);
            }
        }

        if (readFailure is FrameTooLargeException tooLarge)
        {
            throw new ParserSandboxException(ParserFailureKind.OutputTooLarge, "the parser sent an oversize frame: " + tooLarge.Message);
        }

        if (readFailure is not null || terminal is null)
        {
            var exit = naturalExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            string tail;
            lock (stderrTail)
            {
                tail = Untrusted(stderrTail.ToString(), _stderrMessageChars);
            }
            // Classification never reads stderr text: the child controls it. Only the way the process ended counts. An exit by
            // SIGKILL (137) that we did not cause is what the kernel's out-of-memory killer produces; a hostile child can
            // fake it, which only moves its failure from "crash" to "limit".
            var failure = naturalExitCode == _sigkillExitCode && readFailure is null
                ? ParserFailureKind.LimitExceeded
                : ParserFailureKind.Crash;
            throw new ParserSandboxException(
                failure,
                $"the parser process ended without a result (exit code {exit})" + (tail.Length > 0 ? ": " + tail : string.Empty)
                + (readFailure is null ? string.Empty : "; " + Untrusted(readFailure.Message, 200)));
        }

        if (terminal.Type == ParserMessageTypes.Error)
        {
            // A bad-request report is believed only when the child sent no frames and the host exited with code 2;
            // anything else claiming it is a protocol violation and counts as a crash.
            var kind = terminal.ErrorKind switch
            {
                ParserErrorKinds.Limit or ParserErrorKinds.Memory => ParserFailureKind.LimitExceeded,
                ParserErrorKinds.BadRequest => messages.Count == 0 && naturalExitCode is null or 2 ? ParserFailureKind.BadRequest : ParserFailureKind.ProtocolViolation,
                _ => ParserFailureKind.ParserError,
            };
            throw new ParserSandboxException(kind, Untrusted(terminal.Message ?? "parser error", 1024));
        }

        var records = messages.Count(m => m.Type == ParserMessageTypes.Record);
        if (request.Op == ParserOps.Parse && terminal.Count != records)
        {
            throw new ParserSandboxException(ParserFailureKind.ProtocolViolation, $"the parser announced {terminal.Count} records and sent {records}");
        }

        return messages;
    }

    /// <summary>Waits for clean-up work that normally ends with the child, but never longer than the exit grace.</summary>
    private static async Task WaitBoundedAsync(Task task)
    {
        try
        {
            await task.WaitAsync(_exitGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandoned: the caller disposes the process, which closes the pipes under the leftover work.
        }
    }

    /// <summary>Child-controlled text goes into messages only with control characters removed and its length capped.</summary>
    private static string Untrusted(string text, int maxChars)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxChars));
        foreach (var c in text.Trim())
        {
            if (builder.Length >= maxChars) break;
            builder.Append(char.IsControl(c) ? ' ' : c);
        }
        return builder.ToString();
    }

    private static void CheckSourceName(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) > MaxSourceNameBytes)
        {
            throw new ParserSandboxException(ParserFailureKind.LimitExceeded, $"the source name is over {MaxSourceNameBytes} bytes");
        }
        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                throw new ParserSandboxException(ParserFailureKind.LimitExceeded, "the source name contains a control character");
            }
        }
    }

    /// <summary>Every child frame is untrusted: a frame must be complete for its type before the server uses it.</summary>
    private static void ValidateMessage(string op, ParserMessage message)
    {
        switch (message.Type)
        {
            case ParserMessageTypes.Record:
                var r = message.Record;
                if (op != ParserOps.Parse || r is null || r.SourceKey is null || r.Kind is null || r.Title is null || r.Body is null || r.Attributes is null
                    || r.Attributes.Any(a => a.Key is null || a.Value is null))
                {
                    throw new ParserSandboxException(ParserFailureKind.ProtocolViolation, "the parser sent an incomplete record frame");
                }
                break;
            case ParserMessageTypes.DetectResult:
                if (op != ParserOps.Detect || !(message.Confidence >= 0 && message.Confidence <= 1))
                {
                    throw new ParserSandboxException(ParserFailureKind.ProtocolViolation, "the parser sent an invalid detect frame");
                }
                break;
            case ParserMessageTypes.Importers:
                if (op != ParserOps.List || message.Importers is null || message.Importers.Any(i => i is null || string.IsNullOrEmpty(i.Id) || i.Version is null))
                {
                    throw new ParserSandboxException(ParserFailureKind.ProtocolViolation, "the parser sent an invalid importer list");
                }
                break;
            case ParserMessageTypes.Done:
            case ParserMessageTypes.Error:
                break;
            default:
                throw new ParserSandboxException(ParserFailureKind.ProtocolViolation, "the parser sent an unknown frame type");
        }
    }

    private static void WriteInput(Process process, ParserRequest request, byte[] content)
    {
        try
        {
            var stdin = process.StandardInput.BaseStream;
            ParserFrames.Write(stdin, request);
            const int chunk = 64 * 1024;
            for (var offset = 0; offset < content.Length; offset += chunk)
            {
                stdin.Write(content, offset, Math.Min(chunk, content.Length - offset));
            }
            stdin.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child ended before it read everything; the read side reports why.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already closed.
            }
        }
    }

    private LaunchCommand BuildCommand()
    {
        var host = string.IsNullOrEmpty(_options.HostAssemblyPath)
            ? Path.Combine(AppContext.BaseDirectory, _hostFileName)
            : _options.HostAssemblyPath;
        var args = new List<string> { host };
        foreach (var assembly in _options.ImporterAssemblies)
        {
            args.Add("--importer-assembly");
            args.Add(assembly);
        }
        var dotnet = ResolveDotnet(_options.DotnetPath);
        if (!File.Exists(host))
        {
            throw new ParserSandboxException(ParserFailureKind.HostUnavailable, "the parser host assembly was not found: " + host);
        }
        if (Path.IsPathRooted(dotnet) && !File.Exists(dotnet))
        {
            throw new ParserSandboxException(ParserFailureKind.HostUnavailable, "the dotnet host was not found: " + dotnet);
        }
        return new LaunchCommand(dotnet, args);
    }

    private static string ResolveDotnet(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;
        var exe = OperatingSystem.IsWindows() ? configured + ".exe" : configured;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }
        return configured;
    }

    /// <summary>Starts from an empty environment so no secret reaches the child, then adds the few variables it needs.</summary>
    private void ScrubEnvironment(ProcessStartInfo info)
    {
        info.Environment.Clear();
        foreach (var name in _inheritedEnvironment)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        }
        // The managed heap ceiling is the memory limit that works the same on every OS. Value is hexadecimal.
        info.Environment["DOTNET_GCHeapHardLimit"] = _options.MaxMemoryBytes.ToString("x", CultureInfo.InvariantCulture);
        info.Environment["DOTNET_EnableDiagnostics"] = "0";
        info.Environment["DOTNET_gcServer"] = "0";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    }
}
