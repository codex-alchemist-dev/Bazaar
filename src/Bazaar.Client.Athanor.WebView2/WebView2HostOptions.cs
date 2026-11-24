using System.Runtime.Versioning;
using Bazaar.Client.Athanor.Bundle;

[assembly: SupportedOSPlatform("windows")]

namespace Bazaar.Client.Athanor.WebView2;

/// <summary>
/// What a WebView2 editor host needs (doc 57 §3a). The user-data folder is required and has no default, so the client
/// decides where WebView2 keeps its profile and cache (under the client data folder, never in a system profile path).
/// </summary>
public sealed record WebView2HostOptions
{
    /// <summary>The virtual host the bundle is served from. A reserved name that never resolves, so no request can leave the machine.</summary>
    public const string VirtualHost = "athanor.invalid";

    /// <summary>The origin of the editor page.</summary>
    public static readonly Uri Origin = new($"https://{VirtualHost}/");

    /// <summary>The WebView2 user-data folder (profile, cache). One shared environment is created per folder.</summary>
    public required string UserDataFolder { get; init; }

    /// <summary>The bundle folder (page, bridge script, Monaco). Defaults to the folder the build copies next to the assembly.</summary>
    public string? BundleRoot { get; init; }

    /// <summary>Hash the Monaco files against the pinned lock before loading. On by default; turn off only in tests that fake the bundle.</summary>
    public bool VerifyBundle { get; init; } = true;

    /// <summary>Reload the page after a renderer crash so the bridge reports ready again and the host resends its state.</summary>
    public bool ReloadAfterCrash { get; init; } = true;

    /// <summary>Allow the DevTools protocol (not the DevTools window UI). Off for users; the integration tests turn it on to inject input.</summary>
    public bool EnableDevToolsProtocol { get; init; }

    /// <summary>The bundle folder to use, resolved.</summary>
    public string ResolvedBundleRoot => BundleRoot ?? AthanorBundle.DefaultRoot;

    /// <summary>Throws <see cref="ArgumentException"/> when the options cannot be used.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(UserDataFolder) || !Path.IsPathFullyQualified(UserDataFolder))
        {
            throw new ArgumentException("UserDataFolder must be an absolute path chosen by the client.", nameof(UserDataFolder));
        }
    }
}
