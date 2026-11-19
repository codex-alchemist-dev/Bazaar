using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Mentions;

/// <summary>One principal's handle.</summary>
/// <param name="PrincipalId">The principal: an agent id or a human id.</param>
/// <param name="Kind">What kind of principal it is.</param>
/// <param name="Handle">The handle, lowercase, without the "@".</param>
public sealed record PrincipalHandle(Guid PrincipalId, PrincipalKind Kind, string Handle);

/// <summary>The handle does not have the shape <see cref="HandleSlug.IsValid"/> accepts, or is a broadcast word.</summary>
public sealed class InvalidHandleException(string handle)
    : ArgumentException($"'{handle}' is not a valid handle: use {HandleSlug.MinLength} to {HandleSlug.MaxLength} lowercase letters, digits, '_' or '-', starting with a letter or digit; 'here' and 'all' are reserved.")
{
    /// <summary>The rejected handle, after normalisation.</summary>
    public string Handle { get; } = handle;
}

/// <summary>Another principal already holds the handle.</summary>
public sealed class HandleTakenException(string handle, Exception? innerException = null)
    : InvalidOperationException($"The handle '{handle}' is already taken.", innerException)
{
    /// <summary>The handle that is taken.</summary>
    public string Handle { get; } = handle;
}
