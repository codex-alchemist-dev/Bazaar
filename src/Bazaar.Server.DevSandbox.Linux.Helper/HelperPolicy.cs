using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.DevSandbox.Linux.Helper;

/// <summary>
/// The policy file the Linux provider hands the helper (JSON, schema version 1). Unknown properties are an error, so a typo can never
/// silently drop a restriction.
/// </summary>
internal sealed class HelperPolicy
{
    /// <summary>Schema version. Only 1 is accepted.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>Absolute paths the target may read and execute (files or directories). Missing paths are skipped (that only narrows access).</summary>
    [JsonPropertyName("readPaths")]
    public List<string> ReadPaths { get; set; } = [];

    /// <summary>Absolute directories (or files) the target may read, write, create and delete in. Each must exist.</summary>
    [JsonPropertyName("writePaths")]
    public List<string> WritePaths { get; set; } = [];

    /// <summary>Extra syscalls to allow, by name; only names in <see cref="SeccompProgram.Optional"/>.</summary>
    [JsonPropertyName("extraSyscalls")]
    public List<string> ExtraSyscalls { get; set; } = [];

    /// <summary>The value of PATH in the target's environment, which holds nothing else.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "/usr/bin:/bin";

    /// <summary>RLIMIT_AS (soft and hard) applied by the helper just before exec, or null for none. The runtime of the helper itself needs a large address space, so prlimit cannot set this.</summary>
    [JsonPropertyName("addressSpaceBytes")]
    public long? AddressSpaceBytes { get; set; }
}

[JsonSourceGenerationOptions(
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    PropertyNameCaseInsensitive = false,
    WriteIndented = false)]
[JsonSerializable(typeof(HelperPolicy))]
internal sealed partial class HelperPolicyJsonContext : JsonSerializerContext
{
}

/// <summary>Parses and validates a policy. Any problem is an error; nothing is repaired.</summary>
internal static class HelperPolicyParser
{
    /// <summary>Parses <paramref name="json"/>; on failure returns null and sets <paramref name="error"/>.</summary>
    internal static HelperPolicy? Parse(string json, out string? error)
    {
        error = null;
        HelperPolicy? policy;
        try
        {
            policy = JsonSerializer.Deserialize(json, HelperPolicyJsonContext.Default.HelperPolicy);
        }
        catch (JsonException ex)
        {
            error = "policy is not valid JSON for this schema: " + ex.Message;
            return null;
        }

        if (policy is null)
        {
            error = "policy is empty";
            return null;
        }

        error = Validate(policy);
        return error is null ? policy : null;
    }

    internal static string? Validate(HelperPolicy policy)
    {
        if (policy.Version != 1)
        {
            return $"unsupported policy version {policy.Version}";
        }

        if (policy.WritePaths.Count == 0)
        {
            return "writePaths must name at least the per-run work directory";
        }

        foreach (var p in policy.ReadPaths.Concat(policy.WritePaths))
        {
            if (string.IsNullOrEmpty(p) || p[0] != '/' || p.Contains('\0', StringComparison.Ordinal))
            {
                return $"path '{p}' must be absolute and contain no NUL";
            }
        }

        if (policy.WritePaths.Any(p => p == "/"))
        {
            return "writePaths must not contain /";
        }

        if (string.IsNullOrEmpty(policy.Path) || policy.Path.Contains('\0', StringComparison.Ordinal))
        {
            return "path must be a non-empty string without NUL";
        }

        if (policy.AddressSpaceBytes is <= 0)
        {
            return "addressSpaceBytes must be positive when present";
        }

        foreach (var name in policy.ExtraSyscalls)
        {
            if (Array.IndexOf(SeccompProgram.Denied, name) >= 0)
            {
                return $"syscall '{name}' is always denied and cannot be added";
            }

            if (Array.IndexOf(SeccompProgram.Optional, name) < 0)
            {
                return $"syscall '{name}' is not in the optional allow catalogue";
            }
        }

        return null;
    }
}
