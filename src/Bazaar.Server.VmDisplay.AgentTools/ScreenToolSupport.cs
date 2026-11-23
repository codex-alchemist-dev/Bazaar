using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.AgentTools;

/// <summary>
/// Shared argument parsing and result shaping for the two screen tools. Parsing is strict: a field that is not named in the schema is
/// refused, so a model cannot smuggle in a VM id, an agent id or a source the schema does not offer. Nothing here logs.
/// </summary>
internal static class ScreenToolSupport
{
    /// <summary>The refusal that carries a parse problem back to the model.</summary>
    internal sealed class ArgumentProblem(string message) : Exception(message);

    /// <summary>Returns the JSON for a refused call.</summary>
    internal static string Refusal(string status, string message) => new JsonObject
    {
        ["ok"] = false,
        ["status"] = status,
        ["message"] = message,
    }.ToJsonString();

    /// <summary>Returns the JSON for a display failure. The text is fixed per kind: an exception message is never echoed.</summary>
    internal static string Failure(DisplayException ex)
    {
        (string name, string text) = ex.Failure switch
        {
            DisplayFailure.Disabled => ("disabled", "Screen access is turned off on this server."),
            DisplayFailure.Refused => ("refused", "The call was refused by policy. Do not retry it unchanged."),
            DisplayFailure.RateLimited => ("rate_limited", "Too many screen calls just now. Wait a minute before the next one."),
            DisplayFailure.NoSourceAvailable => ("no_source_available", "No screen source is available for that target right now."),
            DisplayFailure.SourceFailed => ("source_failed", "The screen source failed. Try again later or use another source."),
            DisplayFailure.FrameTooLarge => ("frame_too_large", "The frame was too large to handle."),
            DisplayFailure.FrameMalformed => ("frame_malformed", "The source returned an unusable image."),
            DisplayFailure.InvalidInput => ("invalid_input", "The display module rejected the call as invalid."),
            _ => ("unknown", "The display call failed."),
        };
        return new JsonObject
        {
            ["ok"] = false,
            ["status"] = "display_failed",
            ["failure"] = name,
            ["message"] = text,
        }.ToJsonString();
    }

    /// <summary>Throws <see cref="ArgumentProblem"/> for the first property not in <paramref name="allowed"/>.</summary>
    internal static void RejectUnknown(JsonElement obj, string what, params string[] allowed)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (Array.IndexOf(allowed, p.Name) < 0)
                throw new ArgumentProblem($"{what} has an unknown field '{Truncate(p.Name)}'.");
        }
    }

    /// <summary>Reads the <c>target</c> and optional <c>source</c> fields. A browser target uses the browser source only.</summary>
    internal static (AgentDisplayKind Kind, DisplaySourceKind? Source) ReadTargetAndSource(JsonElement args)
    {
        string? target = OptionalString(args, "target", 16);
        AgentDisplayKind kind = target switch
        {
            "linux" => AgentDisplayKind.Linux,
            "windows" => AgentDisplayKind.Windows,
            "browser" => AgentDisplayKind.Browser,
            _ => throw new ArgumentProblem("target must be one of linux, windows, browser."),
        };

        string? source = OptionalString(args, "source", 16, required: false);
        DisplaySourceKind? parsed = source switch
        {
            null => null,
            "hypervisor" => DisplaySourceKind.Hypervisor,
            "guest" => DisplaySourceKind.Guest,
            "browser" => DisplaySourceKind.Browser,
            _ => throw new ArgumentProblem("source must be one of hypervisor, guest, browser."),
        };

        if (kind == AgentDisplayKind.Browser)
        {
            if (parsed is not (null or DisplaySourceKind.Browser))
                throw new ArgumentProblem("The browser target only uses the browser source.");
            return (kind, DisplaySourceKind.Browser);
        }

        if (parsed == DisplaySourceKind.Browser)
            throw new ArgumentProblem("The browser source is only for the browser target.");
        return (kind, parsed);
    }

    /// <summary>
    /// Resolves the caller's own target. Returns <see langword="null"/> and a refusal string when the agent has no such target, or when
    /// the resolver returned a target owned by another agent (which must never happen, and is refused rather than trusted).
    /// </summary>
    internal static async Task<(DisplayTarget? Target, string? Refusal)> ResolveOwnAsync(
        IAgentDisplayTargets targets, Guid agentId, AgentDisplayKind kind, CancellationToken cancellationToken)
    {
        DisplayTarget? target = await targets.ResolveAsync(agentId, kind, cancellationToken).ConfigureAwait(false);
        if (target is null)
            return (null, Refusal("no_target", $"You have no {KindName(kind)} to use."));

        if (!string.Equals(target.AgentId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return (null, Refusal("no_target", $"You have no {KindName(kind)} to use."));

        if (kind == AgentDisplayKind.Browser && string.IsNullOrEmpty(target.BrowserSessionId))
            return (null, Refusal("no_target", "You have no open browser session to use."));

        return (target, null);
    }

    /// <summary>The wire name of a source.</summary>
    internal static string SourceName(DisplaySourceKind source) => source switch
    {
        DisplaySourceKind.Hypervisor => "hypervisor",
        DisplaySourceKind.Guest => "guest",
        DisplaySourceKind.Browser => "browser",
        _ => "unknown",
    };

    /// <summary>Reads a string field. A missing field throws when <paramref name="required"/>, else returns <see langword="null"/>.</summary>
    internal static string? OptionalString(JsonElement obj, string name, int maxLength, bool required = true)
    {
        if (!obj.TryGetProperty(name, out JsonElement e))
        {
            if (required)
                throw new ArgumentProblem($"{name} is required.");
            return null;
        }

        if (e.ValueKind != JsonValueKind.String)
            throw new ArgumentProblem($"{name} must be a string.");
        string value;
        try
        {
            value = e.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape in the JSON text cannot be decoded.
            throw new ArgumentProblem($"{name} is not valid Unicode.");
        }

        if (value.Length > maxLength)
            throw new ArgumentProblem($"{name} is too long.");
        return value;
    }

    /// <summary>Reads an integer field within an inclusive range.</summary>
    internal static int ReadInt(JsonElement obj, string name, int min, int max, int? fallback = null)
    {
        if (!obj.TryGetProperty(name, out JsonElement e))
        {
            if (fallback is int f)
                return f;
            throw new ArgumentProblem($"{name} is required.");
        }

        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v))
            throw new ArgumentProblem($"{name} must be an integer.");
        if (v < min || v > max)
            throw new ArgumentProblem($"{name} must be {min} to {max}.");
        return v;
    }

    private static string KindName(AgentDisplayKind kind) => kind switch
    {
        AgentDisplayKind.Linux => "Linux VM",
        AgentDisplayKind.Windows => "Windows VM",
        _ => "browser session",
    };

    /// <summary>Shortens a model-supplied name for an error message and replaces anything but letters, digits and underscores.</summary>
    private static string Truncate(string s) =>
        string.Create(Math.Min(s.Length, 40), s, static (span, src) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = char.IsAsciiLetterOrDigit(src[i]) || src[i] == '_' ? src[i] : '?';
        });
}
