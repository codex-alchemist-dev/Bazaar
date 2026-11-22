using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.TalentPool;

/// <summary>Validates Talent manifests and hashes them (docs/design/25: manifest only, no memory, whitelisted models).</summary>
public static class ManifestValidator
{
    /// <summary>Async so the whitelist can read the gateway. Throws <see cref="ManifestRejectedException"/> when the manifest breaks a rule.</summary>
    public static async Task ValidateAsync(SoulManifest manifest, ITalentModelWhitelist whitelist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(whitelist);

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new ManifestRejectedException("A template needs a name.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Prompt))
        {
            throw new ManifestRejectedException("A template needs a prompt.");
        }

        if (manifest.ModelWhitelist is null || manifest.ModelWhitelist.Count == 0)
        {
            throw new ManifestRejectedException("A template needs at least one model.");
        }

        foreach (var model in manifest.ModelWhitelist)
        {
            if (string.IsNullOrWhiteSpace(model) || !await whitelist.IsAllowedAsync(model, cancellationToken).ConfigureAwait(false))
            {
                throw new ManifestRejectedException($"Model '{model}' is not on the install whitelist.");
            }
        }

        foreach (var file in manifest.Files ?? [])
        {
            if (IsMemoryFile(file))
            {
                throw new ManifestRejectedException($"File '{file}' is memory. A template holds no memory.");
            }
        }
    }

    /// <summary>The stable template id for a name: lowercase, with runs of other characters turned into a single dash.</summary>
    public static string TemplateIdFor(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var id = sb.ToString().TrimEnd('-');
        if (id.Length == 0)
        {
            throw new ManifestRejectedException("The template name has no letters or digits.");
        }

        return id;
    }

    /// <summary>Lowercase hex SHA-256 of the manifest in a canonical, order-insensitive form.</summary>
    public static string ContentSha(SoulManifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append("name=").Append(manifest.Name.Trim()).Append('\n');
        sb.Append("prompt=").Append(manifest.Prompt).Append('\n');
        sb.Append("tools=").AppendJoin(',', manifest.ToolAllowlist.Order(StringComparer.Ordinal)).Append('\n');
        sb.Append("models=").AppendJoin(',', manifest.ModelWhitelist.Order(StringComparer.Ordinal)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static bool IsMemoryFile(string path)
    {
        var p = path.Replace('\\', '/').Trim().ToLowerInvariant();
        return p.StartsWith("memory/", StringComparison.Ordinal)
            || p.Contains("/memory/", StringComparison.Ordinal)
            || p.StartsWith("memory.", StringComparison.Ordinal)
            || Path.GetFileName(p).StartsWith("memory", StringComparison.Ordinal);
    }
}
