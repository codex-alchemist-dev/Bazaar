using System.Text;

namespace Bazaar.Server.Athanor;

/// <summary>Text checks and the language hint table for editor files.</summary>
public static class EditorText
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Dictionary<string, string> _languages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".md"] = "markdown",
        [".json"] = "json",
        [".js"] = "javascript",
        [".ts"] = "typescript",
        [".lua"] = "lua",
        [".luau"] = "lua",
        [".gd"] = "gdscript",
        [".py"] = "python",
        [".rs"] = "rust",
        [".go"] = "go",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".xml"] = "xml",
        [".axaml"] = "xml",
        [".csproj"] = "xml",
        [".html"] = "html",
        [".css"] = "css",
        [".sql"] = "sql",
        [".toml"] = "toml",
        [".sh"] = "shell",
        [".ps1"] = "powershell",
        [".txt"] = "plaintext",
    };

    /// <summary>True when the bytes are not UTF-8 text: invalid UTF-8 or a NUL byte.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOf((byte)0) >= 0)
        {
            return true;
        }

        try
        {
            _ = _strictUtf8.GetCharCount(bytes);
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    /// <summary>True when the text holds a NUL character, or an unpaired surrogate that cannot be written as UTF-8.</summary>
    public static bool IsBinary(string text)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _ = _strictUtf8.GetByteCount(text);
            return false;
        }
        catch (EncoderFallbackException)
        {
            return true;
        }
    }

    /// <summary>Decodes UTF-8 bytes already known to be text.</summary>
    public static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>UTF-8 size of the text.</summary>
    public static long ByteCount(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>The language id for a path's extension, or null.</summary>
    public static string? LanguageHint(string path) =>
        _languages.TryGetValue(Path.GetExtension(path), out var language) ? language : null;
}
