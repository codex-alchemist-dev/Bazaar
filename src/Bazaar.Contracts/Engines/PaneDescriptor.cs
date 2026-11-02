using System.Text.Json;

namespace Bazaar.Contracts.Engines;

/// <summary>
/// A declarative client pane contributed by an adapter (docs/design/20-engine-integration-api.md, Panes).
/// <c>Kind</c> is namespaced (for example <c>godot.scene-tree</c>, <c>roblox.explorer</c>) and selects a view on the client;
/// <c>Model</c> is the JSON data the view renders. <c>Revision</c> numbers the snapshot so a reconnecting client can
/// tell which snapshot is current. Snapshots are not GC replay.
/// </summary>
public sealed record PaneDescriptor(
    string Kind,
    string Title,
    EngineCapability Capability,
    JsonElement Model,
    long Revision = 0);
