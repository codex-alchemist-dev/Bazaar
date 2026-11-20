using Bazaar.Contracts.Modules;
using Cdax.Core;

namespace Bazaar.Client.Modules;

/// <summary>Why a module was not loaded.</summary>
/// <param name="ModuleId">The manifest id, or an empty string when the module has no manifest.</param>
/// <param name="Code">Stable code: <c>no-manifest</c>, <c>invalid-manifest</c>, <c>host-incompatible</c>, <c>duplicate-id</c>, <c>missing-dependency</c>, <c>dependency-cycle</c>, <c>pane-kind-taken</c>, <c>command-taken</c> or <c>register-failed</c>.</param>
/// <param name="Message">One line for the log.</param>
public sealed record ClientModuleSkip(string ModuleId, string Code, string Message);

/// <summary>The outcome of loading a list of client modules.</summary>
public sealed class ClientModuleLoadResult
{
    internal ClientModuleLoadResult(
        IReadOnlyList<ModuleManifest> loaded,
        IReadOnlyList<ClientModuleSkip> skipped,
        IReadOnlyDictionary<string, string> paneKindOwners,
        IReadOnlyDictionary<string, string> commandOwners,
        IReadOnlyList<ClientCommandContribution> commands)
    {
        Commands = commands;
        Loaded = loaded;
        Skipped = skipped;
        PaneKindOwners = paneKindOwners;
        CommandOwners = commandOwners;
    }

    /// <summary>Manifests of the modules that registered, in load order.</summary>
    public IReadOnlyList<ModuleManifest> Loaded { get; }

    /// <summary>Modules that were left out, with the reason. A skipped module costs only its own features.</summary>
    public IReadOnlyList<ClientModuleSkip> Skipped { get; }

    /// <summary>
    /// Keeps, from <paramref name="modules"/>, only the entries that loaded, in their original order. A head composes a
    /// module's other contracts (for example its pane module) from what this returns, so a skipped module adds nothing.
    /// </summary>
    public IReadOnlyList<T> Only<T>(IEnumerable<T> modules)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(modules);
        var ids = new HashSet<string>(Loaded.Select(manifest => manifest.Id), StringComparer.Ordinal);
        return [.. modules.Where(module => module is IClientModule client && ids.Contains(client.Manifest.Id))];
    }

    /// <summary>Pane kind to the id of the module that registered it.</summary>
    public IReadOnlyDictionary<string, string> PaneKindOwners { get; }

    /// <summary>Every command the loaded modules contributed with its default mnemonics, in load order.</summary>
    public IReadOnlyList<ClientCommandContribution> Commands { get; }

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
        var contributions = new List<ClientCommandContribution>();

        void Skip(string id, string code, string message)
        {
            skipped.Add(new ClientModuleSkip(id, code, message));
            report?.Invoke($"Bazaar: module '{id}' not loaded ({code}): {message}");
        }

        // Pass 1: checks that need no other module. A failure skips only that module.
        var candidates = new Dictionary<string, IClientModule>(StringComparer.Ordinal);
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

            if (!candidates.TryAdd(manifest.Id, module!))
            {
                Skip(manifest.Id, "duplicate-id", "another module with this id is already loaded.");
            }
        }

        // Pass 2: hard dependencies. A module whose dependency is missing or was itself skipped is skipped too, and that can
        // cascade, so repeat until nothing changes. Soft dependencies never skip a module.
        bool changed;
        do
        {
            changed = false;
            foreach (var (id, module) in candidates.ToList())
            {
                var missing = module.Manifest.Hard.FirstOrDefault(dependency => !candidates.ContainsKey(dependency));
                if (missing is not null)
                {
                    candidates.Remove(id);
                    Skip(id, "missing-dependency", $"it needs module '{missing}', which is not loaded.");
                    changed = true;
                }
            }
        }
        while (changed);

        // Pass 3: the same graph the server uses (Cdax.Core), so a client module and a server module share one dependency model.
        // Hard cycles skip every module caught in or behind the cycle; the rest still load, in dependency order.
        var graph = candidates.Values.Select(module => ToComponent(module.Manifest)).ToList();
        var stuck = FindStuck(graph);
        foreach (var id in stuck.Order(StringComparer.Ordinal))
        {
            candidates.Remove(id);
            Skip(id, "dependency-cycle", "it is part of, or depends on, a dependency cycle.");
        }

        var ordered = ComponentGraph.Order(graph.Where(component => !stuck.Contains(component.Id)));

        // Pass 4: register in dependency order, with the checks that compare one module against those already loaded.
        foreach (var component in ordered)
        {
            var module = candidates[component.Id];
            var manifest = module.Manifest;

            var notLoaded = manifest.Hard.FirstOrDefault(dependency => !ids.Contains(dependency));
            if (notLoaded is not null)
            {
                Skip(manifest.Id, "missing-dependency", $"it needs module '{notLoaded}', which did not load.");
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

            IReadOnlyList<ClientCommandContribution> registered;
            try
            {
                var builder = new ClientModuleBuilder(manifest);
                module.Register(builder);
                registered = builder.Contributions;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Skip(manifest.Id, "register-failed", ex.Message);
                continue;
            }

            ids.Add(manifest.Id);
            contributions.AddRange(registered);
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

        return new ClientModuleLoadResult(loaded, skipped, paneKinds, commands, contributions);
    }

    /// <summary>Describes a client module to the shared graph: the same fields a server component declares.</summary>
    private static ComponentManifest ToComponent(ModuleManifest manifest) =>
        new(manifest.Id, manifest.Version, ComponentKind.ClientPane, manifest.Hard, [.. manifest.PaneKinds, .. manifest.Commands], manifest.Soft);

    /// <summary>The ids that cannot be placed because they sit on a hard dependency cycle or depend on one.</summary>
    private static HashSet<string> FindStuck(IReadOnlyList<ComponentManifest> graph)
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        bool progressed;
        do
        {
            progressed = false;
            foreach (var component in graph.Where(c => !placed.Contains(c.Id) && c.DependsOn.All(placed.Contains)))
            {
                placed.Add(component.Id);
                progressed = true;
            }
        }
        while (progressed);

        return [.. graph.Select(c => c.Id).Where(id => !placed.Contains(id))];
    }
}
