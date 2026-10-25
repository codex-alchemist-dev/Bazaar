namespace Bazaar.Server.Secrets;

/// <summary>A bootstrap secret reference could not be resolved. The message names the reference, never the secret value.</summary>
public sealed class SecretResolutionException : Exception
{
    public SecretResolutionException(string reference, string reason, Exception? innerException = null)
        : base($"Secret reference '{reference}' could not be resolved: {reason}", innerException)
    {
        Reference = reference;
    }

    /// <summary>The reference that failed, such as <c>env:BAZAAR_DB</c>.</summary>
    public string Reference { get; }
}
