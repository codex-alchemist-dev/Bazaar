namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>Thrown when the TPM cannot seal or unseal the KEK. The provider fails closed on it, never falls back to another provider. The message never holds key material.</summary>
public sealed class KekCustodyException(string message, Exception? inner = null) : Exception(message, inner);
