using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Council.Data;

namespace Bazaar.Server.Tests.Council.Data;

/// <summary>
/// TEST FAKE for <see cref="ICouncilReasoningCipher"/>. It XORs with a random per-voter key and nonce so tests can check
/// that the store seals reasoning and opens it again. It is not cryptography and must never ship in production code.
/// </summary>
internal sealed class CouncilTestCipher : ICouncilReasoningCipher
{
    private readonly Dictionary<string, Guid> _keyIdByVoter = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, byte[]> _keyById = [];

    public ReasoningCiphertext Encrypt(string voterId, string plaintext)
    {
        if (!_keyIdByVoter.TryGetValue(voterId, out var keyId))
        {
            keyId = Guid.CreateVersion7();
            _keyIdByVoter[voterId] = keyId;
            _keyById[keyId] = RandomNumberGenerator.GetBytes(32);
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = Xor(Encoding.UTF8.GetBytes(plaintext), _keyById[keyId], nonce);
        return new ReasoningCiphertext(keyId, ciphertext, nonce);
    }

    public string Decrypt(string voterId, ReasoningCiphertext sealedText) =>
        Encoding.UTF8.GetString(Xor(sealedText.Ciphertext, _keyById[sealedText.KeyId], sealedText.Nonce));

    private static byte[] Xor(byte[] data, byte[] key, byte[] nonce)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ key[i % key.Length] ^ nonce[i % nonce.Length]);
        return result;
    }
}
