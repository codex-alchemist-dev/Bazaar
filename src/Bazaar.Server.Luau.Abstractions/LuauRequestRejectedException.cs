namespace Bazaar.Server.Luau.Abstractions;

/// <summary>
/// A check request was refused before any tool ran: the root is missing, a file is empty, missing, outside the root,
/// or reached through a link below the root.
/// </summary>
public sealed class LuauRequestRejectedException(string message) : Exception(message);
