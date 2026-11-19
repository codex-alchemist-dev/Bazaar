using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Bazaar.Server.Mail.Parsing;
using Bazaar.Server.Mail.Quarantine;

namespace Bazaar.Server.Mail.Tests.Quarantine;

/// <summary>Lookalike-sender and nested-archive screens (doc 22 WP3), with synthetic corpus items only.</summary>
public class QuarantineScreensTests
{
    // Synthetic names only; nothing here is a real operator or brand.
    private static readonly string[] _protected = ["example.org", "mail.example.net"];

    private static InboundHeaders Message(string from, params AttachmentInfo[] attachments) => new(
        From: from,
        SpfResult: "pass",
        DkimResult: "pass",
        DmarcResult: "pass",
        MessageBytes: 2048,
        Attachments: attachments,
        Links: []);

    private static InboundQuarantineOptions Options() => new() { ProtectedDomains = _protected };

    private static QuarantineReason[] Reasons(InboundHeaders headers, InboundQuarantineOptions? options = null) =>
        InboundQuarantineRules.Evaluate(headers, options ?? Options()).Reasons.ToArray();

    [Theory]
    [InlineData("exarnple.org")] // rn looks like m
    [InlineData("examp1e.org")] // digit one for l
    [InlineData("example.0rg")] // not the TLD rule, edit distance one
    [InlineData("exampel.org")] // adjacent swap
    [InlineData("exmple.org")] // deletion
    [InlineData("examplee.org")] // insertion
    [InlineData("xn--exmple-cua.org")] // punycode for a non-ASCII near copy
    [InlineData("EXAMPLE.ORG.evil.test", false)] // a different domain that merely contains the name is not flagged
    public void Lookalike_sender_domains_are_held(string domain, bool expected = true)
    {
        var reasons = Reasons(Message($"Boss <boss@{domain}>"));

        Assert.Equal(expected, reasons.Contains(QuarantineReason.LookalikeSenderDomain));
    }

    [Fact]
    public void Unicode_homoglyph_domain_is_a_lookalike_when_given_directly()
    {
        // The address parser accepts ASCII domains only, so headers carry the punycode form; the detector handles both.
        Assert.True(LookalikeDomainDetector.IsLookalike("exаmple.org", _protected)); // Cyrillic a
        Assert.True(LookalikeDomainDetector.IsLookalike("ехаmple.org", _protected)); // Cyrillic e, x, a
        Assert.False(LookalikeDomainDetector.IsLookalike("example.org", _protected));
        Assert.False(LookalikeDomainDetector.IsLookalike("", _protected));
    }

    [Fact]
    public void Punycode_form_of_a_homoglyph_domain_is_held()
    {
        var puny = new System.Globalization.IdnMapping().GetAscii("exаmple.org"); // Cyrillic a

        Assert.StartsWith("xn--", puny, StringComparison.Ordinal);
        Assert.Contains(QuarantineReason.LookalikeSenderDomain, Reasons(Message($"a@{puny}")));
    }

    [Theory]
    [InlineData("example.org")]
    [InlineData("EXAMPLE.org")]
    [InlineData("news.example.org")]
    [InlineData("mail.example.net")]
    [InlineData("unrelated.test")]
    [InlineData("ex.org")]
    public void Protected_domains_their_subdomains_and_unrelated_domains_pass(string domain)
    {
        Assert.DoesNotContain(QuarantineReason.LookalikeSenderDomain, Reasons(Message($"a@{domain}")));
    }

    [Fact]
    public void Short_protected_names_are_not_edit_distance_checked()
    {
        var options = new InboundQuarantineOptions { ProtectedDomains = ["ab.io"] };

        Assert.DoesNotContain(QuarantineReason.LookalikeSenderDomain, Reasons(Message("a@ac.io"), options));
    }

    [Fact]
    public void Without_protected_domains_the_screen_is_off()
    {
        var decision = InboundQuarantineRules.Evaluate(Message("a@exarnple.org"), new InboundQuarantineOptions());

        Assert.Equal(QuarantineAction.Deliver, decision.Action);
    }

    [Fact]
    public void Lookalike_reasons_sort_after_the_older_reasons()
    {
        var headers = Message("a@exarnple.org") with { SpfResult = "fail" };

        Assert.Equal(
            new[] { QuarantineReason.AuthenticationFailed, QuarantineReason.LookalikeSenderDomain },
            Reasons(headers));
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entryStream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static AttachmentInfo Attached(byte[] zip, MailLimits? limits = null) =>
        new("files.zip", "application/zip", ZipArchiveInspector.Inspect(zip, limits ?? MailLimits.Defaults));

    [Fact]
    public void Plain_archive_passes()
    {
        var zip = Zip(("a.txt", "hello"u8.ToArray()), ("b.txt", "world"u8.ToArray()));

        Assert.Empty(Reasons(Message("a@unrelated.test", Attached(zip))));
    }

    [Fact]
    public void Archive_inside_archive_is_held_without_unpacking()
    {
        var inner = Zip(("a.txt", "hello"u8.ToArray()));
        var outer = Zip(("inner.zip", inner));

        Assert.Equal(new[] { QuarantineReason.NestedArchive }, Reasons(Message("a@unrelated.test", Attached(outer))));
    }

    [Fact]
    public void Compression_bomb_is_held_on_declared_ratio()
    {
        // 8 MiB of zeros compresses to a few KiB; the directory declares it, nothing is expanded.
        var zip = Zip(("zeros.bin", new byte[8 * 1024 * 1024]));
        var facts = ZipArchiveInspector.Inspect(zip, MailLimits.Defaults);

        Assert.NotNull(facts);
        Assert.False(facts.Malformed);
        Assert.Equal(8L * 1024 * 1024, facts.UncompressedBytes);
        Assert.Contains(QuarantineReason.NestedArchive, Reasons(Message("a@unrelated.test", Attached(zip))));
    }

    [Fact]
    public void Archive_declaring_too_many_bytes_is_held()
    {
        var zip = Zip(("zeros.bin", new byte[1024 * 1024]));
        var options = new InboundQuarantineOptions { Limits = new MailLimits { MaxArchiveUncompressedBytes = 1024, MaxArchiveCompressionRatio = int.MaxValue } };

        Assert.Contains(QuarantineReason.NestedArchive, Reasons(Message("a@unrelated.test", Attached(zip)), options));
    }

    [Fact]
    public void Archive_with_too_many_entries_is_held_without_reading_them()
    {
        var entries = Enumerable.Range(0, 20).Select(i => ($"f{i}.txt", "x"u8.ToArray())).ToArray();
        var limits = new MailLimits { MaxArchiveEntries = 10 };
        var facts = ZipArchiveInspector.Inspect(Zip(entries), limits);

        Assert.NotNull(facts);
        Assert.True(facts.Malformed);
        Assert.Equal(20, facts.EntryCount);
        Assert.Equal(0, facts.UncompressedBytes);

        var options = new InboundQuarantineOptions { Limits = limits };
        Assert.Contains(QuarantineReason.NestedArchive, Reasons(Message("a@unrelated.test", new AttachmentInfo("x.zip", null, facts)), options));
    }

    [Fact]
    public void Lying_directory_sizes_do_not_expand_anything()
    {
        // Hand-built directory declaring 4 GiB minus one from a 10-byte entry: a bomb shape, held, never decompressed.
        var zip = RawZip("bomb.bin", compressed: 10, uncompressed: 0xFFFFFFFE);
        var facts = ZipArchiveInspector.Inspect(zip, MailLimits.Defaults);

        Assert.NotNull(facts);
        Assert.Equal(0xFFFFFFFEL, facts.UncompressedBytes);
        Assert.Contains(QuarantineReason.NestedArchive, Reasons(Message("a@unrelated.test", new AttachmentInfo("b.zip", null, facts))));
    }

    [Fact]
    public void Zero_compressed_size_with_declared_data_is_held()
    {
        var zip = RawZip("odd.bin", compressed: 0, uncompressed: 1000);

        Assert.Contains(QuarantineReason.NestedArchive, Reasons(Message("a@unrelated.test", Attached(zip))));
    }

    [Fact]
    public void Truncated_directory_is_malformed()
    {
        var zip = RawZip("a.txt", compressed: 1, uncompressed: 1);
        // Keep the end record but point the directory past the data.
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16), 0x00FFFFFF);

        var facts = ZipArchiveInspector.Inspect(zip, MailLimits.Defaults);

        Assert.NotNull(facts);
        Assert.True(facts.Malformed);
    }

    [Fact]
    public void Non_zip_bytes_and_short_inputs_are_not_archives()
    {
        Assert.Null(ZipArchiveInspector.Inspect("MZ plain bytes"u8, MailLimits.Defaults));
        Assert.Null(ZipArchiveInspector.Inspect([], MailLimits.Defaults));
    }

    [Fact]
    public void Garbage_with_an_end_signature_never_throws()
    {
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var data = new byte[64 + random.Next(200)];
            random.NextBytes(data);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(data.Length - 22), 0x06054b50);
            data.AsSpan(data.Length - 2).Clear();

            _ = ZipArchiveInspector.Inspect(data, MailLimits.Defaults);
        }
    }

    [Fact]
    public void Archive_limits_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MailLimits { MaxArchiveEntries = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MailLimits { MaxArchiveUncompressedBytes = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MailLimits { MaxArchiveCompressionRatio = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InboundQuarantineRules.Evaluate(Message("a@x.test"), new InboundQuarantineOptions { Limits = new MailLimits { MaxArchiveEntries = 0 } }));
    }

    [Fact]
    public void Nested_multipart_and_malformed_mime_still_parse_within_limits()
    {
        // Corpus: deep nesting past the limit is held by the parser, not expanded.
        var builder = new StringBuilder("From: a@unrelated.test\r\nSubject: x\r\nMIME-Version: 1.0\r\n");
        const int depth = 12;
        for (var i = 0; i < depth; i++)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"Content-Type: multipart/mixed; boundary=\"b{i}\"\r\n\r\n--b{i}\r\n");
        }

        builder.Append("Content-Type: text/plain\r\n\r\nhi\r\n");
        var raw = Encoding.ASCII.GetBytes(builder.ToString());

        var parser = new MimeMailParser();
        Assert.Throws<MailLimitException>(() => parser.Parse(raw));
    }

    // A minimal one-entry zip with chosen declared sizes and no real data.
    private static byte[] RawZip(string name, uint compressed, uint uncompressed)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var directory = new byte[46 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(directory, 0x02014b50);
        BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(20), compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(24), uncompressed);
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(28), (ushort)nameBytes.Length);
        nameBytes.CopyTo(directory, 46);

        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)directory.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), 0);
        return [.. directory, .. end];
    }
}
