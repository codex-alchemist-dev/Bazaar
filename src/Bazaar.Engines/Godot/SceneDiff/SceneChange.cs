namespace Bazaar.Engines.Godot.SceneDiff;

/// <summary>One difference between two Godot text resources (.tscn or .tres).</summary>
public abstract record SceneChange;

/// <summary>A node path that exists only in the after file.</summary>
/// <param name="Path">Node path relative to the scene root; "." is the root.</param>
/// <param name="Type">The node's type, or null for an instanced node written without a type.</param>
public sealed record NodeAdded(string Path, string? Type) : SceneChange;

/// <summary>A node path that exists only in the before file.</summary>
/// <param name="Path">Node path relative to the scene root; "." is the root.</param>
public sealed record NodeRemoved(string Path) : SceneChange;

/// <summary>
/// A property whose raw text differs. <paramref name="Key"/> is the property name; keys beginning with "@" are header
/// attributes (for example "@type" or "@instance"). A null value means the key is absent on that side.
/// </summary>
public sealed record PropertyChanged(string Path, string Key, string? Before, string? After) : SceneChange;

/// <summary>
/// An ext_resource or sub_resource section (Kind), or the main [resource] section (Kind "resource", Id empty),
/// that exists only in the after file. Resources are identified by kind and id.
/// </summary>
public sealed record ResourceAdded(string Kind, string Id) : SceneChange;

/// <summary>A resource section that exists only in the before file. See <see cref="ResourceAdded"/>.</summary>
public sealed record ResourceRemoved(string Kind, string Id) : SceneChange;

/// <summary>
/// A header attribute or property of a resource section whose raw text differs. Changing a resource's id is reported
/// as a removal plus an addition, not as a change.
/// </summary>
public sealed record ResourceChanged(string Kind, string Id, string Key, string? Before, string? After) : SceneChange;
