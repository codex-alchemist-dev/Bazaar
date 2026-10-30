namespace Bazaar.Server.Components;

/// <summary>A component graph that cannot start: duplicate ids, unknown or disabled dependencies, or a cycle.</summary>
public sealed class ComponentGraphException(string message) : InvalidOperationException(message);
