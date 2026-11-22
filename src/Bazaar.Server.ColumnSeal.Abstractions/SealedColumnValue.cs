namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// The stored form of one sealed value. <see cref="Ciphertext"/> is the ciphertext followed by the 16-byte GCM tag and goes in
/// <c>&lt;column&gt;_ct bytea</c>; <see cref="Nonce"/> is the 12-byte nonce and goes in <c>&lt;column&gt;_nonce bytea</c>.
/// <see cref="KeyId"/> names the DEK; it is part of the authenticated data, so the row must keep it (doc 63 is silent on the
/// column, so the feature migration chooses; one <c>dek_id</c> per row is expected).
/// </summary>
public readonly record struct SealedColumnValue(ReadOnlyMemory<byte> Ciphertext, ReadOnlyMemory<byte> Nonce, Guid KeyId);
