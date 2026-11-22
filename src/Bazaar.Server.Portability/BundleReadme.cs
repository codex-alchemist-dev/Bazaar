using System.Globalization;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>
/// Writes the <c>README.txt</c> of an export bundle (doc 44 section 5): a plain-language note that says what the bundle is and what
/// each folder holds. It is generated, it is not authoritative (the manifest is), and the same inputs always give the same bytes.
/// </summary>
/// <remarks>
/// Only the top-level folders that are in the bundle are described, so the note never talks about data that is not there. The
/// folder wording is ours: doc 44 fixes the folder names, not their descriptions.
/// </remarks>
public static class BundleReadme
{
    /// <summary>The file name, at the bundle root.</summary>
    public const string FileName = "README.txt";

    private static readonly (string Folder, string Text)[] _folders =
    [
        ("gc", "Group chats. One folder per chat, named by the chat's id. Each holds the chat's events, messages, every version of every message, who was mentioned, and a summary of where the chat ended."),
        ("vault", "The Vault: the company's written notes and documents, as plain Markdown files, plus a git bundle that keeps their history."),
        ("kanban", "Task boards and their cards."),
        ("graph", "The knowledge graph (the Constellation): its nodes and links. Anything marked as derived can be rebuilt and is not the original record."),
        ("personal", "Personal details about people. These files are locked (encrypted) and cannot be read without the matching key, unless a file is listed as readable below."),
        ("audit", "The audit log: the record of what was done, and by whom, with signed checkpoints."),
        ("config", "Settings."),
        ("souls", "Agent profiles."),
        ("usage", "Usage records."),
        ("reputation", "Reputation records."),
        ("blobs", "Attached files, stored as they were uploaded."),
    ];

    /// <summary>
    /// Builds the note. <paramref name="paths"/> are the content files already in the bundle, and <paramref name="plaintextPaths"/>
    /// are the ones among them that hold unlocked personal data. Output is UTF-8 with LF line endings and no byte order mark.
    /// </summary>
    public static byte[] Build(
        string instanceId,
        DateTimeOffset createdAt,
        IReadOnlyCollection<string> paths,
        IReadOnlyCollection<string> plaintextPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(plaintextPaths);

        // Top-level folders only: a path with no slash is a loose file, not a folder.
        var present = new HashSet<string>(
            paths.Where(p => p.Contains('/', StringComparison.Ordinal)).Select(p => p[..p.IndexOf('/', StringComparison.Ordinal)]),
            StringComparer.Ordinal);
        var text = new StringBuilder();
        text.Append("This is a copy of company data, saved from a Bazaar server.\n");
        text.Append("Instance: ").Append(instanceId).Append('\n');
        text.Append("Saved at: ").Append(createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append('\n');
        text.Append('\n');
        text.Append("Everything here is in ordinary open formats: text, Markdown, JSON, JSON Lines and git. You can open every\n");
        text.Append("file with any text editor. You do not need Bazaar to read it.\n");
        text.Append('\n');
        text.Append("This note is only a guide. The file manifest.json is the authoritative list of what belongs in this bundle.\n");

        text.Append('\n');
        text.Append("What is in the folders\n");
        var described = 0;
        foreach (var (folder, description) in _folders)
        {
            if (present.Remove(folder))
            {
                text.Append("  ").Append(folder).Append("/  ").Append(description).Append('\n');
                described++;
            }
        }

        // A folder this note has no words for is still named, so nothing in the bundle goes unmentioned.
        foreach (var folder in present.Order(StringComparer.Ordinal))
        {
            text.Append("  ").Append(folder).Append("/  Not described in this note.\n");
            described++;
        }

        if (described == 0)
        {
            text.Append("  This bundle holds no data folders.\n");
        }

        text.Append('\n');
        text.Append("The loose files at the top\n");
        text.Append("  manifest.json  The list of every file here, with its size and a fingerprint (SHA-256) so changes can be spotted.\n");
        text.Append("  manifest.sig   The server's signature over manifest.json, which shows the list was not altered after it was written.\n");
        text.Append("  manifest.pub   The public key that checks that signature.\n");
        text.Append("  README.txt     This note.\n");

        if (plaintextPaths.Count > 0)
        {
            text.Append('\n');
            text.Append("Readable personal data\n");
            text.Append("  The files below hold personal data in plain, readable form. A later request to erase a person cannot reach\n");
            text.Append("  these copies. Keep them safe, or delete them when you no longer need them.\n");
            foreach (var path in plaintextPaths.Order(StringComparer.Ordinal))
            {
                text.Append("    ").Append(path).Append('\n');
            }
        }

        return ExportText.Utf8.GetBytes(text.ToString());
    }
}
