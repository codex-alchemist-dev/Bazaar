using System.Collections.Frozen;

namespace Bazaar.Client.Athanor;

/// <summary>
/// The immutable language table that picks a Monaco language id for a document (doc 57 §5). Lookup order:
/// well-known file name, then extension, then the first-line hint (shebang or <c>&lt;?xml</c>), then plaintext.
/// Language servers are not part of the registry; Monaco's built-in services are the only ones in v1.
/// </summary>
public sealed class LanguageRegistry
{
    private readonly FrozenDictionary<string, string> _byExtension;
    private readonly FrozenDictionary<string, string> _byFileName;
    private readonly FrozenDictionary<string, string> _byInterpreter;

    /// <summary>Builds a registry from a table. Throws <see cref="ArgumentException"/> when two rows claim the same key.</summary>
    public LanguageRegistry(IEnumerable<LanguageDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var rows = definitions.ToList();
        Definitions = rows.AsReadOnly();
        _byExtension = Index(rows, d => d.Extensions, "extension");
        _byFileName = Index(rows, d => d.FileNames, "file name");
        _byInterpreter = Index(rows, d => d.Interpreters, "interpreter");
    }

    /// <summary>The table this registry was built from, in order.</summary>
    public IReadOnlyList<LanguageDefinition> Definitions { get; }

    /// <summary>The shipped table, covering the languages listed in doc 57 §5 and the Monaco ids they map to.</summary>
    public static LanguageRegistry Default { get; } = new(DefaultTable());

    /// <summary>
    /// Returns the language id for a normalised workspace path, using <paramref name="firstLine"/> only when the
    /// name and extension do not decide it. Unknown input returns <see cref="LanguageIds.PlainText"/>.
    /// </summary>
    public string Resolve(string path, string? firstLine = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];

        if (_byFileName.TryGetValue(name, out var byName))
        {
            return byName;
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1 && _byExtension.TryGetValue(name[(dot + 1)..], out var byExtension))
        {
            return byExtension;
        }

        return ResolveFirstLine(firstLine) ?? LanguageIds.PlainText;
    }

    /// <summary>Returns the language id a first line implies (shebang or XML declaration), or null when it implies none.</summary>
    public string? ResolveFirstLine(string? firstLine)
    {
        if (string.IsNullOrEmpty(firstLine))
        {
            return null;
        }

        var line = firstLine.TrimStart('\uFEFF', ' ', '\t');
        if (line.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            return LanguageIds.Xml;
        }

        if (!line.StartsWith("#!", StringComparison.Ordinal))
        {
            return null;
        }

        var tokens = line[2..].Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var interpreter = Interpreter(tokens[0]);
        if (interpreter == "env")
        {
            // "#!/usr/bin/env -S python3": the first argument that is not a flag names the interpreter.
            var next = tokens.Skip(1).FirstOrDefault(t => !t.StartsWith('-'));
            if (next is null)
            {
                return null;
            }

            interpreter = Interpreter(next);
        }

        return _byInterpreter.TryGetValue(interpreter, out var language) ? language : null;
    }

    private static string Interpreter(string token)
    {
        var slash = token.LastIndexOf('/');
        var name = slash < 0 ? token : token[(slash + 1)..];

        // Strip a version suffix such as python3.12 or node18.
        var end = name.Length;
        while (end > 0 && (char.IsAsciiDigit(name[end - 1]) || name[end - 1] == '.'))
        {
            end--;
        }

        return end == 0 ? name : name[..end];
    }

    private static FrozenDictionary<string, string> Index(
        List<LanguageDefinition> rows,
        Func<LanguageDefinition, IReadOnlyList<string>> keys,
        string kind)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var key in keys(row))
            {
                if (!map.TryAdd(key, row.LanguageId))
                {
                    throw new ArgumentException($"The {kind} '{key}' is claimed by more than one language.");
                }
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static List<LanguageDefinition> DefaultTable() =>
    [
        new(LanguageIds.CSharp, ["cs", "csx"], [], []),
        new(LanguageIds.FSharp, ["fs", "fsi", "fsx"], [], []),
        new(LanguageIds.TypeScript, ["ts", "tsx", "mts", "cts"], [], []),
        new(LanguageIds.JavaScript, ["js", "jsx", "mjs", "cjs"], [], ["node", "nodejs", "deno", "bun"]),
        new(LanguageIds.Json, ["json"], [], []),
        new(LanguageIds.JsonC, ["jsonc"], [], []),
        new(LanguageIds.Markdown, ["md", "markdown"], [], []),
        new(LanguageIds.Yaml, ["yml", "yaml"], [], []),
        new(LanguageIds.Toml, ["toml"], [], []),
        new(LanguageIds.Xml, ["xml", "xaml", "csproj", "props", "targets", "slnx", "svg", "xsd", "xsl"], [], []),
        new(LanguageIds.Html, ["html", "htm"], [], []),
        new(LanguageIds.Css, ["css"], [], []),
        new(LanguageIds.Sql, ["sql"], [], []),
        new(LanguageIds.Python, ["py", "pyw"], [], ["python"]),
        new(LanguageIds.Rust, ["rs"], [], []),
        new(LanguageIds.Go, ["go"], [], []),
        new(LanguageIds.Lua, ["lua", "luau"], [], ["lua", "luau"]),
        new(LanguageIds.GDScript, ["gd"], [], []),
        new(LanguageIds.Shell, ["sh", "bash", "zsh"], [], ["sh", "bash", "zsh", "dash", "ksh", "ash"]),
        new(LanguageIds.PowerShell, ["ps1", "psm1", "psd1"], [], ["pwsh", "powershell"]),
        new(LanguageIds.Dockerfile, ["dockerfile"], ["Dockerfile", "Containerfile"], []),
        new(LanguageIds.Ini, ["ini", "cfg"], [], []),
        new(LanguageIds.Makefile, [], ["Makefile", "GNUmakefile"], []),
        new(LanguageIds.PlainText, ["txt"], [], []),
    ];
}
