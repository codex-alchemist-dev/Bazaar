namespace Bazaar.Server.Identity.Keys;

/// <summary>Thrown when a wrapped DEK cannot be unwrapped. The message names the failure, never the key material.</summary>
public sealed class KekUnwrapException : Exception
{
    public KekUnwrapException(string message)
        : base(message)
    {
    }

    public KekUnwrapException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
