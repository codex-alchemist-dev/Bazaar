using System.Diagnostics;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Diagnostics.Capture;

/// <summary>
/// Turns a failure into a <see cref="CaptureRecord"/> (doc 43 section 4). The capture is deterministic: it reads
/// the exception's stack, the log snapshot the caller passes in, and the client information. It does no I/O, no
/// model call and no clock read. Every string passes through the redactor. A redaction step that throws, or
/// returns a failed or residual status, stores the placeholder for that field and holds the record.
/// </summary>
public sealed class ClientCapture
{
    private const string _unknown = "<unknown>";

    private readonly IFieldRedactor _redactor;
    private readonly IClientInfoProvider _clientInfo;
    private readonly Func<string, bool> _isAppModule;

    /// <param name="redactor">The field redactor, normally a <see cref="Redactor"/> built with the runtime exact values.</param>
    /// <param name="clientInfo">The source of app version, device and module information.</param>
    /// <param name="isAppModule">Decides whether a module is a Bazaar module, by assembly simple name. The default is a <c>Bazaar.</c> prefix.</param>
    public ClientCapture(IFieldRedactor redactor, IClientInfoProvider clientInfo, Func<string, bool>? isAppModule = null)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(clientInfo);
        _redactor = redactor;
        _clientInfo = clientInfo;
        _isAppModule = isAppModule ?? IsBazaarModule;
    }

    /// <summary>Builds the capture record for one failure.</summary>
    /// <param name="exception">The failure. Its stack is read from the exception's own trace; a never-thrown exception has no stack.</param>
    /// <param name="errorCode">The registered error code, or null for an unhandled exception.</param>
    /// <param name="logs">The log snapshot, oldest first. Taken by the caller, so this method does not touch the buffer.</param>
    /// <param name="capturedAt">The capture time, given by the caller.</param>
    public CaptureRecord Capture(Exception exception, string? errorCode, IReadOnlyList<CaptureLogEntry> logs, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logs);

        var scrubber = new FieldScrubber(_redactor);
        var rawFrames = ReadFrames(exception);
        var flagged = FlaggedIndex(rawFrames);

        var stack = rawFrames
            .Select(frame => new CaptureFrame(
                scrubber.Clean(RedactionClass.Identifier, frame.Module),
                scrubber.Clean(RedactionClass.Identifier, frame.Type),
                scrubber.Clean(RedactionClass.Identifier, frame.Method),
                frame.App))
            .ToArray();

        var code = errorCode is null ? null : scrubber.Clean(RedactionClass.Identifier, errorCode);
        var exceptionType = scrubber.Clean(RedactionClass.Identifier, exception.GetType().FullName ?? exception.GetType().Name);
        var message = scrubber.Clean(RedactionClass.FreeText, exception.Message);

        var fingerprintFrames = stack.Select(frame => new FingerprintFrame(frame.Module, frame.Type, frame.Method, frame.App)).ToArray();
        var fingerprint = Fingerprint.Compute(new FingerprintInput(code, exceptionType, fingerprintFrames, message));
        var topFrame = fingerprintFrames.Length > 0 ? Fingerprint.FrameKey(fingerprintFrames[0]) : null;

        var redactedLogs = logs
            .Select(entry => new CaptureLogEntry(
                entry.Time,
                scrubber.Clean(RedactionClass.Safe, entry.Level),
                scrubber.Clean(RedactionClass.Identifier, entry.Category),
                scrubber.Clean(RedactionClass.FreeText, entry.Message)))
            .ToArray();

        var client = RedactClientInfo(scrubber, _clientInfo.Current());

        return new CaptureRecord(
            capturedAt,
            code,
            exceptionType,
            message,
            stack,
            flagged,
            topFrame,
            redactedLogs,
            client,
            fingerprint,
            scrubber.Held);
    }

    /// <summary>The frames of the exception's trace, top first. Names only; no file information is requested.</summary>
    private CaptureFrame[] ReadFrames(Exception exception)
    {
        var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
        if (frames is null || frames.Length == 0)
        {
            return [];
        }

        var result = new CaptureFrame[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            // DiagnosticMethodInfo reads the names without reflection, so this stays trimming and AOT safe.
            var method = DiagnosticMethodInfo.Create(frames[i]);
            var module = SimpleName(method?.DeclaringAssemblyName) ?? _unknown;
            result[i] = new CaptureFrame(
                module,
                method?.DeclaringTypeName ?? _unknown,
                method?.Name ?? _unknown,
                _isAppModule(module));
        }

        return result;
    }

    /// <summary>The first Bazaar frame, or the throwing frame (index 0) when there is none. Null without a stack.</summary>
    private static int? FlaggedIndex(CaptureFrame[] frames)
    {
        if (frames.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < frames.Length; i++)
        {
            if (frames[i].App)
            {
                return i;
            }
        }

        return 0;
    }

    private static ClientInfo RedactClientInfo(FieldScrubber scrubber, ClientInfo info)
    {
        var device = new DeviceInfo(
            scrubber.Clean(RedactionClass.Identifier, info.Device.OsDescription),
            scrubber.Clean(RedactionClass.Identifier, info.Device.RuntimeDescription),
            scrubber.Clean(RedactionClass.Safe, info.Device.Architecture));
        var modules = info.Modules
            .Select(module => new ModuleInfo(
                scrubber.Clean(RedactionClass.Identifier, module.Name),
                scrubber.Clean(RedactionClass.Identifier, module.Version)))
            .ToArray();
        return new ClientInfo(scrubber.Clean(RedactionClass.Safe, info.AppVersion), device, modules);
    }

    /// <summary>The simple name of an assembly full name: the text before the first comma.</summary>
    private static string? SimpleName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return null;
        }

        var comma = fullName.IndexOf(',', StringComparison.Ordinal);
        return (comma < 0 ? fullName : fullName[..comma]).Trim();
    }

    private static bool IsBazaarModule(string module) => module.StartsWith("Bazaar.", StringComparison.Ordinal);

    /// <summary>Redacts fields one at a time and records whether any field came back held. Fails closed.</summary>
    private sealed class FieldScrubber(IFieldRedactor redactor)
    {
        /// <summary>True once any field has been replaced by a failed or residual marker.</summary>
        public bool Held { get; private set; }

        public string Clean(RedactionClass fieldClass, string? text)
        {
            RedactionResult result;
            try
            {
                result = redactor.Redact(fieldClass, text);
            }
            catch (Exception)
            {
                Held = true;
                return RedactionMarkers.Failed;
            }

            // The text of a non-ok result is never used, even if a redactor returned the original.
            switch (result.Status)
            {
                case RedactionStatus.Ok:
                    return result.Text;
                case RedactionStatus.Residual:
                    Held = true;
                    return RedactionMarkers.Residual;
                default:
                    Held = true;
                    return RedactionMarkers.Failed;
            }
        }
    }
}
