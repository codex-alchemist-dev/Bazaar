using System.Security.Cryptography;
using Bazaar.Server.Security.Erasure;

namespace Bazaar.Server.Tests.Security.Erasure;

public class SubjectRefHasherTests
{
    private static readonly byte[] _salt = new byte[16];
    private static readonly byte[] _otherSalt = Enumerable.Repeat((byte)0x7f, 16).ToArray();

    [Fact]
    public void Hash_gives_the_same_output_for_the_same_inputs()
    {
        Assert.Equal(
            SubjectRefHasher.Hash("person-123", _salt),
            SubjectRefHasher.Hash("person-123", _salt));
    }

    [Fact]
    public void Hash_is_a_32_byte_digest()
    {
        Assert.Equal(32, SubjectRefHasher.Hash("person-123", _salt).Length);
    }

    [Fact]
    public void Hash_gives_different_output_for_different_salts()
    {
        Assert.NotEqual(
            SubjectRefHasher.Hash("person-123", _salt),
            SubjectRefHasher.Hash("person-123", _otherSalt));
    }

    [Fact]
    public void Hash_gives_different_output_for_different_subjects()
    {
        Assert.NotEqual(
            SubjectRefHasher.Hash("person-123", _salt),
            SubjectRefHasher.Hash("person-124", _salt));
    }

    [Fact]
    public void Hash_is_not_the_unsalted_digest_of_the_subject()
    {
        var unsalted = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("person-123"));

        Assert.NotEqual(unsalted, SubjectRefHasher.Hash("person-123", _salt));
    }

    [Fact]
    public void Hash_rejects_a_salt_shorter_than_the_minimum()
    {
        var short15 = new byte[SubjectRefHasher.MinSaltLength - 1];

        Assert.Throws<ArgumentException>(() => SubjectRefHasher.Hash("person-123", short15));
    }

    [Fact]
    public void Hash_rejects_an_empty_salt()
    {
        Assert.Throws<ArgumentException>(() => SubjectRefHasher.Hash("person-123", []));
    }

    [Fact]
    public void Hash_rejects_an_empty_subject_id()
    {
        Assert.Throws<ArgumentException>(() => SubjectRefHasher.Hash("", _salt));
        Assert.Throws<ArgumentException>(() => SubjectRefHasher.Hash("   ", _salt));
    }
}
