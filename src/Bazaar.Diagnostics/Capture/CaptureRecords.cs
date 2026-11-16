namespace Bazaar.Diagnostics.Capture;

/// <summary>One log or event entry as the capture keeps it. The message is a template, not an interpolated text.</summary>
/// <param name="Time">When the entry was written.</param>
/// <param name="Level">The log level name.</param>
/// <param name="Category">The logging category.</param>
/// <param name="Message">The message template.</param>
public sealed record CaptureLogEntry(DateTimeOffset Time, string Level, string Category, string Message);

/// <summary>One stack frame. Names only: no locals, no arguments, no file paths or line numbers.</summary>
/// <param name="Module">The assembly simple name that declares the method.</param>
/// <param name="Type">The declaring type's full name, as the runtime reports it.</param>
/// <param name="Method">The method name, as the runtime reports it.</param>
/// <param name="App">True when the module is a Bazaar module.</param>
public sealed record CaptureFrame(string Module, string Type, string Method, bool App);

/// <summary>
/// The deterministic capture of one failure (doc 43 sections 4.1 and 5.1). Every string has passed through the
/// redactor. When <see cref="Held"/> is true, a redaction step failed or the residual scan matched, and the
/// record must be held rather than sent.
/// </summary>
/// <param name="CapturedAt">The time the caller gave for the capture.</param>
/// <param name="ErrorCode">The registered error code, or null for an unhandled exception.</param>
/// <param name="ExceptionType">The exception type's full name.</param>
/// <param name="Message">The exception message.</param>
/// <param name="Stack">The stack, top frame first. Inner exceptions are not included yet.</param>
/// <param name="FlaggedFrame">The index of the first Bazaar frame, or index 0 when there is none. Null when there is no stack.</param>
/// <param name="NormalisedTopFrame">The top frame in the fingerprint's normalised form, or null when there is no stack.</param>
/// <param name="Logs">The log snapshot, oldest first.</param>
/// <param name="Client">The app version, device information and module versions.</param>
/// <param name="Fingerprint">The <c>fp1</c> fingerprint, computed from the redacted fields.</param>
/// <param name="Held">True when a redaction step failed or the residual scan matched.</param>
public sealed record CaptureRecord(
    DateTimeOffset CapturedAt,
    string? ErrorCode,
    string ExceptionType,
    string Message,
    IReadOnlyList<CaptureFrame> Stack,
    int? FlaggedFrame,
    string? NormalisedTopFrame,
    IReadOnlyList<CaptureLogEntry> Logs,
    ClientInfo Client,
    string Fingerprint,
    bool Held);
