using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>
/// The security floor for a <c>.wsb</c> document, checked before it is written or launched (doc 50 section 9). A Windows Sandbox
/// switch that is left out defaults to the permissive value (clipboard sharing, for one), so the document must set every switch
/// explicitly and may carry nothing else: no logon command, no unknown element, and no mapped folder that is writable,
/// relative or a network path. <see cref="WindowsSandboxConfig.Build"/> output always passes; a hand-written or tampered
/// document that loosens anything does not.
/// </summary>
public static partial class WindowsSandboxConfigPolicy
{
    private const int _maxDocumentChars = 64 * 1024;

    private static readonly string[] _mustBeDisabled = ["VGpu", "ClipboardRedirection", "AudioInput", "VideoInput", "PrinterRedirection"];

    private static readonly HashSet<string> _allowedElements = new(StringComparer.Ordinal)
    {
        "VGpu", "Networking", "MemoryInMB", "ClipboardRedirection", "AudioInput", "VideoInput", "PrinterRedirection", "ProtectedClient", "MappedFolders",
    };

    /// <summary>True when <paramref name="path"/> is a local absolute Windows path with a drive letter: not UNC, not relative, no <c>..</c>, no stream or control characters.</summary>
    public static bool IsSafeHostFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Length <= 260
        && LocalWindowsPath().IsMatch(path)
        && !path.Split('\\').Any(segment => segment is ".." or "." || segment.EndsWith(' ') || segment.EndsWith('.'));

    /// <summary>Throws <see cref="ArgumentException"/> when the document is not valid <c>.wsb</c> XML or loosens the floor.</summary>
    public static void Validate(string configXml)
    {
        ArgumentNullException.ThrowIfNull(configXml);
        if (configXml.Length > _maxDocumentChars)
        {
            throw Refuse("the document is too large");
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(configXml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = _maxDocumentChars });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw Refuse("it is not valid XML: " + ex.Message);
        }

        XElement root = document.Root ?? throw Refuse("it has no root element");
        if (root.Name.LocalName != "Configuration" || root.Name.Namespace != XNamespace.None)
        {
            throw Refuse("the root element must be Configuration");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement element in root.Elements())
        {
            string name = element.Name.LocalName;
            if (!_allowedElements.Contains(name) || element.Name.Namespace != XNamespace.None)
            {
                throw Refuse($"element '{name}' is not allowed (a logon command or unknown setting is never accepted)");
            }

            if (!seen.Add(name))
            {
                throw Refuse($"element '{name}' appears twice");
            }
        }

        foreach (string name in _mustBeDisabled)
        {
            Require(root, name, "Disable");
        }

        Require(root, "ProtectedClient", "Enable");

        string networking = RequiredValue(root, "Networking");
        if (!networking.Equals("Disable", StringComparison.OrdinalIgnoreCase) && !networking.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse($"Networking must be Disable or Default, not '{networking}'");
        }

        if (!int.TryParse(RequiredValue(root, "MemoryInMB"), NumberStyles.None, CultureInfo.InvariantCulture, out int memory) || memory <= 0)
        {
            throw Refuse("MemoryInMB must be a positive whole number");
        }

        if (root.Element("MappedFolders") is { } mapped)
        {
            foreach (XElement folder in mapped.Elements())
            {
                ValidateMappedFolder(folder);
            }
        }
    }

    private static void ValidateMappedFolder(XElement folder)
    {
        if (folder.Name.LocalName != "MappedFolder")
        {
            throw Refuse($"element '{folder.Name.LocalName}' is not allowed inside MappedFolders");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement child in folder.Elements())
        {
            if (child.Name.LocalName is not ("HostFolder" or "SandboxFolder" or "ReadOnly") || !seen.Add(child.Name.LocalName))
            {
                throw Refuse($"mapped folder element '{child.Name.LocalName}' is not allowed or appears twice");
            }
        }

        if (!IsSafeHostFolder(folder.Element("HostFolder")?.Value))
        {
            throw Refuse("a mapped host folder must be a local absolute path with a drive letter (no network path, no '..')");
        }

        if (!IsSafeHostFolder(folder.Element("SandboxFolder")?.Value))
        {
            throw Refuse("a mapped sandbox folder must be an absolute path with a drive letter");
        }

        if (!string.Equals(folder.Element("ReadOnly")?.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse("a mapped folder must be read-only (ReadOnly true, stated explicitly)");
        }
    }

    private static void Require(XElement root, string name, string expected)
    {
        string value = RequiredValue(root, name);
        if (!value.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse($"{name} must be {expected}, not '{value}'");
        }
    }

    private static string RequiredValue(XElement root, string name) =>
        root.Element(name)?.Value.Trim() is { Length: > 0 } value ? value : throw Refuse($"{name} must be set explicitly");

    private static ArgumentException Refuse(string reason) => new("Windows Sandbox config refused: " + reason + ".");

    [GeneratedRegex(@"^[A-Za-z]:\\[^<>:""|?*\x00-\x1f]*$")]
    private static partial Regex LocalWindowsPath();
}
