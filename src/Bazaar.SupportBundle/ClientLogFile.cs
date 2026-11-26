using System.Text;

namespace Bazaar.SupportBundle;

/// <summary>
/// Copies everything written to a text writer (the head hands it <c>Console.Error</c>) into the client log file, one
/// timestamped line per write, so the support bundle and the clean-run check have a log to read. Lines from stderr are
/// <c>WARN</c>; code that reports a failure calls <see cref="Error"/>. A file over 4 MiB is started again on open.
/// </summary>
public sealed class ClientLogFile : TextWriter
{
    private const long _maxBytesOnOpen = 4 * 1024 * 1024;
    private readonly TextWriter _inner;
    private readonly StreamWriter _file;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private ClientLogFile(TextWriter inner, StreamWriter file, TimeProvider clock)
    {
        _inner = inner;
        _file = file;
        _clock = clock;
    }

    /// <inheritdoc />
    public override Encoding Encoding => _inner.Encoding;

    /// <summary>Opens (creating folders) the log file at <paramref name="path"/> and tees <paramref name="inner"/> into it.</summary>
    public static ClientLogFile Open(string path, TextWriter inner, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var mode = File.Exists(path) && new FileInfo(path).Length > _maxBytesOnOpen ? FileMode.Create : FileMode.Append;
        var stream = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new ClientLogFile(inner, new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }, clock ?? TimeProvider.System);
    }

    /// <summary>Writes an <c>ERROR</c> line to the file only. The clean-run check fails on any such line.</summary>
    public void Error(string message) => Append("ERROR", message);

    /// <inheritdoc />
    public override void Write(char value) => _inner.Write(value);

    /// <inheritdoc />
    public override void Write(string? value) => _inner.Write(value);

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        _inner.WriteLine(value);
        Append("WARN", value ?? string.Empty);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Append(string level, string message)
    {
        lock (_gate)
        {
            _file.WriteLine($"{_clock.GetUtcNow():yyyy-MM-ddTHH:mm:ssZ} {level} {message}");
        }
    }
}
