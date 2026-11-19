using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Modules;

/// <summary>Why a module was not loaded.</summary>
/// <param name="ModuleId">The manifest id, or an empty string when the module has no manifest.</param>
/// <param name="Code">Stable code: <c>no-manifest</c>, <c>invalid-manifest</c>, <c>host-incompatible</c>, <c>duplicate-id</c>, <c>pane-kind-taken</c>, <c>command-taken</c> or <c>register-failed</c>.</param>
/// <param name="Message">One line for the log.</param>
public sealed record ClientModuleSkip(string ModuleId, string Code, string Message);

/// <summary>The outcome of loading a list of client modules.</summary>
public sealed class ClientModuleLoadResult
{
    internal ClientModuleLoadResult(
        IReadOnlyList<ModuleManifest> loaded,
        IReadOnlyList<ClientModuleSkip> skipped,
        IReadOnlyDictionary<string, string> paneKindOwners,
        IReadOnlyDictionary<string, string> commandOwners)
    {
        Loaded = loaded;
        Skipped = skipped;
        PaneKindOwners = paneKindOwners;
        CommandOwners = commandOwners;
    }

    /// <summary>Manifests of the modules that registered, in load order.</summary>
    public IReadOnlyList<ModuleManifest> Loaded { get; }

    /// <summary>Modules that were left out, with the reason. A skipped module costs only its own features.</summary>
    public IReadOnlyList<ClientModuleSkip> Skipped { get; }

    /// <summary>Pane kind to the id of the module that registered it.</summary>
    public IReadOnlyDictionary<string, string> PaneKindOwners { get; }

    /// <summary>Command id to the id of the module that registered it.</summary>
    public IReadOnlyDictionary<string, string> CommandOwners { get; }
}

/// <summary>
/// The host side of the client module contract (doc 41 sections 4.2 and 8, doc 41b section 2): checks every module's
/// manifest the same way, whoever wrote it, and calls <see cref="IClientModule.Register"/> once for each module that
/// passes. A module that fails a check is skipped and reported; the others still load, so one bad module costs only its
/// own features. Pure logic, no Avalonia and no I/O.
/// </summary>
public static class ClientModuleHost
{
    /// <summary>Validates and registers <paramref name="modules"/> in order.</summary>
    /// <param name="modules">The modules the head composes, in the order they should register.</param>
    /// <param name="host">What this head runs on, for each manifest's host requirements.</param>
    /// <param name="report">Receives one line per skipped module. Null reports nothing.</param>
    public static ClientModuleLoadResult Load(IEnumerable<IClientModule> modules, HostFacts host, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(host);

        var loaded = new List<ModuleManifest>();
        var skipped = new List<ClientModuleSkip>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paneKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);

        void Skip(string id, string code, string message)
        {
            skipped.Add(new ClientModuleSkip(id, code, message));
            report?.Invoke($"Bazaar: module '{id}' not loaded ({code}): {message}");
        }

        foreach (var module in modules)
        {
            var manifest = module?.Manifest;
            if (manifest is null)
            {
                Skip(string.Empty, "no-manifest", "the module declares no manifest.");
                continue;
            }

            var validation = ModuleManifestValidator.Validate(manifest);
            if (!validation.IsValid)
            {
                Skip(manifest.Id, "invalid-manifest", string.Join("; ", validation.Issues.Select(issue => $"{issue.Path}: {issue.Code}")));
                continue;
            }

            var incompatible = HostCompatibility.Check(manifest.Host, host);
            if (incompatible.Count > 0)
            {
                Skip(manifest.Id, "host-incompatible", string.Join("; ", incompatible.Select(issue => issue.Code)));
                continue;
            }

            if (ids.Contains(manifest.Id))
            {
                Skip(manifest.Id, "duplicate-id", "another module with this id is already loaded.");
                continue;
            }

            var takenKind = manifest.PaneKinds.FirstOrDefault(paneKinds.ContainsKey);
            if (takenKind is not null)
            {
                Skip(manifest.Id, "pane-kind-taken", $"pane kind '{takenKind}' belongs to '{paneKinds[takenKind]}'.");
                continue;
            }

            var takenCommand = manifest.Commands.FirstOrDefault(commands.ContainsKey);
            if (takenCommand is not null)
            {
                Skip(manifest.Id, "command-taken", $"command '{takenCommand}' belongs to '{commands[takenCommand]}'.");
                continue;
            }

            try
            {
                module!.Register(new ClientModuleBuilder(manifest));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Skip(manifest.Id, "register-failed", ex.Message);
                continue;
            }

            ids.Add(manifest.Id);
            foreach (var kind in manifest.PaneKinds)
            {
                paneKinds[kind] = manifest.Id;
            }

            foreach (var command in manifest.Commands)
            {
                commands[command] = manifest.Id;
            }

            loaded.Add(manifest);
        }

        return new ClientModuleLoadResult(loaded, skipped, paneKinds, commands);
    }
}
