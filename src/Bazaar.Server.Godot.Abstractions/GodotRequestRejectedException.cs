namespace Bazaar.Server.Godot.Abstractions;

/// <summary>
/// A request was refused before any process ran: the root or a path is invalid, outside the root or reached through a link,
/// or an export preset is unknown or unsafe.
/// </summary>
public sealed class GodotRequestRejectedException(string message) : Exception(message);
