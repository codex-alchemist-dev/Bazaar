using System.Text;
using System.Text.Json;

namespace Bazaar.Client.CompanySetup;

/// <summary>
/// Writes the setup plan JSON (doc 45 section 1.5) for a <see cref="CompanySetupRequest"/>. Only the keys the screen collects are
/// written; the server fills every other decision with its shipped default and then hashes the normalized plan, so the hash the
/// server returns is the one that matters. No secret value is ever written: the key-encryption-key is a reference.
/// </summary>
public static class SetupPlanText
{
    /// <summary>The plan schema version this client writes (<c>SetupConstants.SchemaVersion</c> on the server).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The company starts blank: there is no template or import step on this screen.</summary>
    public const string BlankSource = "blank";

    /// <summary>The plan JSON for <paramref name="request"/>.</summary>
    public static string Build(CompanySetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", SchemaVersion);

            json.WriteStartObject("install");
            json.WriteString("companyName", request.CompanyName);
            json.WriteString("companySlug", request.CompanySlug);
            json.WriteString("locale", request.Locale);
            json.WriteString("timezone", request.Timezone);
            json.WriteEndObject();

            json.WriteStartObject("custody");
            json.WriteString("kekProvider", request.KekProvider);
            json.WriteString("kekRef", request.KekReference);
            json.WriteEndObject();

            json.WriteStartObject("owner");
            json.WriteString("displayName", request.OwnerName);
            json.WriteEndObject();

            json.WriteStartObject("backups");
            json.WriteBoolean("acknowledgeNoTarget", request.AcknowledgeNoBackupTarget);
            json.WriteEndObject();

            json.WriteStartObject("firstRun");
            json.WriteString("defaultAgentName", request.AgentName);
            json.WriteString("defaultAgentPurpose", request.AgentPurpose);
            json.WriteBoolean("acceptConstitution", request.AcceptConstitution);
            json.WriteEndObject();

            json.WriteStartObject("company");
            json.WriteString("source", BlankSource);
            json.WriteEndObject();

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// Derives a slug from a company name: lower-case letters and digits, runs of anything else become one dash, trimmed, at most 63
    /// characters. The person can edit it; the server checks the final value.
    /// </summary>
    public static string SlugFrom(string? name)
    {
        var slug = new StringBuilder();
        var pendingDash = false;
        foreach (var c in name ?? "")
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && slug.Length > 0) slug.Append('-');
                pendingDash = false;
                slug.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingDash = true;
            }

            if (slug.Length >= 63) break;
        }

        return slug.ToString().TrimEnd('-');
    }
}
