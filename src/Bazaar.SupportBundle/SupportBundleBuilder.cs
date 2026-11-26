using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.SupportBundle;

/// <summary>Size limits for a bundle (doc 37 section 4d).</summary>
/// <param name="MaxFileBytes">A larger file keeps its newest lines only. Default 2 MiB.</param>
/// <param name="MaxTotalBytes">Total redacted text in the zip, before compression. Default 5 MiB. Files past it are left out and listed.</param>
public sealed record SupportBundleLimits(int MaxFileBytes = 2 * 1024 * 1024, int MaxTotalBytes = 5 * 1024 * 1024);

/// <summary>
/// Builds a support bundle zip, schema <c>bazaar.supportbundle/1</c>. Everything is redacted line by line with the doc 43
/// redactor before it is written; a line that fails redaction becomes a marker. The same inputs give the same bytes.
/// Nothing here sends anything anywhere.
/// </summary>
public sealed class SupportBundleBuilder
{
    /// <summary>The bundle schema id.</summary>
    public const string Schema = "bazaar.supportbundle/1";

    private static readonly DateTimeOffset _fixedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Redactor _redactor;
    private readonly SupportBundleLimits _limits;

    /// <param name="exactValues">Values replaced everywhere, for example the machine name and user name.</param>
    /// <param name="limits">Size limits. Null uses the defaults.</param>
    public SupportBundleBuilder(IEnumerable<string>? exactValues = null, SupportBundleLimits? limits = null)
    {
        _redactor = new Redactor(exactValues);
        _limits = limits ?? new SupportBundleLimits();
    }

    /// <summary>Collects every source, redacts, and writes the zip to <paramref name="output"/>.</summary>
    public void Build(IEnumerable<ISupportBundleSource> sources, Stream output)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(output);

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();

        foreach (var source in sources.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            IReadOnlyList<SupportBundleFile> collected;
            try
            {
                collected = source.Collect();
            }
            catch (Exception ex)
            {
                notes.Add($"source '{source.Name}' failed: {ex.GetType().Name}");
                continue;
            }

            foreach (var file in collected)
            {
                if (!IsSafePath(file.Path) || file.Path == "manifest.json")
                {
                    notes.Add($"source '{source.Name}' gave a refused path");
                    continue;
                }

                if (!files.TryAdd(file.Path, Clean(file.Text, notes, file.Path)))
                {
                    notes.Add($"duplicate path '{file.Path}' from source '{source.Name}' ignored");
                }
            }
        }

        var total = 0;
        var included = new List<string>();
        foreach (var (path, text) in files.ToArray())
        {
            var size = Encoding.UTF8.GetByteCount(text);
            if (total + size > _limits.MaxTotalBytes)
            {
                files.Remove(path);
                notes.Add($"left out '{path}' (total size limit)");
                continue;
            }

            total += size;
            included.Add(path);
        }

        files["manifest.json"] = ManifestJson(included, notes);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (path, text) in files)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            entry.LastWriteTime = _fixedTime;
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    private string Clean(string text, List<string> notes, string path)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var redacted = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var result = _redactor.Redact(RedactionClass.FreeText, line);
            redacted.Add(result.Status != RedactionStatus.Ok ? RedactionMarkers.Failed : result.Text);
        }

        // Keep the newest lines when a file is too large.
        var bytes = redacted.Sum(l => Encoding.UTF8.GetByteCount(l) + 1);
        if (bytes > _limits.MaxFileBytes)
        {
            var kept = new Stack<string>();
            var used = 0;
            for (var i = redacted.Count - 1; i >= 0; i--)
            {
                var size = Encoding.UTF8.GetByteCount(redacted[i]) + 1;
                if (used + size > _limits.MaxFileBytes)
                {
                    break;
                }

                used += size;
                kept.Push(redacted[i]);
            }

            notes.Add($"cut '{path}' to its newest {kept.Count} lines");
            redacted = [.. kept];
        }

        return string.Join('\n', redacted);
    }

    private string ManifestJson(IReadOnlyList<string> included, IReadOnlyList<string> notes)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteStartArray("files");
            foreach (var path in included)
            {
                w.WriteStringValue(path);
            }

            w.WriteEndArray();
            w.WriteStartArray("notes");
            foreach (var note in notes.Order(StringComparer.Ordinal))
            {
                // Notes can name a path or exception type chosen by a source, so they pass the redactor too.
                w.WriteStringValue(_redactor.Redact(RedactionClass.FreeText, note).Text);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsSafePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && !path.StartsWith('/')
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
}
