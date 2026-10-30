namespace Bazaar.Client.LocalInference.Models;

/// <summary>A manifest is not valid JSON for the manifest schema, or breaks a manifest rule.</summary>
public sealed class ModelFormatException : Exception
{
    public ModelFormatException(string message)
        : base(message)
    {
    }

    public ModelFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Why a model failed an integrity check.</summary>
public enum ModelIntegrityFailure
{
    SignatureMissing,
    SignatureInvalid,
    Missing,
    LengthMismatch,
    HashMismatch,
}

/// <summary>A signature, file or hash did not match the manifest. The model is rejected, never used.</summary>
public sealed class ModelIntegrityException : Exception
{
    public ModelIntegrityException(ModelIntegrityFailure failure, string? fileName, string message)
        : base(message)
    {
        Failure = failure;
        FileName = fileName;
    }

    public ModelIntegrityFailure Failure { get; }

    /// <summary>The manifest file name that failed, or <c>null</c> for a signature failure.</summary>
    public string? FileName { get; }
}

/// <summary>A store operation was refused: unknown model, already installed, or file not listed in the manifest.</summary>
public sealed class ModelStoreException : Exception
{
    public ModelStoreException(string message)
        : base(message)
    {
    }
}
