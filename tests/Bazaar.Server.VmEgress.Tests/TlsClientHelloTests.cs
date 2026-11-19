using Bazaar.Server.VmEgress.Proxy;

namespace Bazaar.Server.VmEgress.Tests;

public class TlsClientHelloTests
{
    private static TlsHelloResult Parse(byte[] bytes) => TlsClientHello.Parse(bytes);

    [Fact]
    public void A_valid_hello_yields_its_name()
    {
        var r = Parse(HelloBuilder.Hello("example.com"));

        Assert.Equal(TlsHelloStatus.Ok, r.Status);
        Assert.Equal("example.com", r.ServerName);
    }

    [Fact]
    public void Case_and_trailing_dot_are_returned_as_sent()
    {
        Assert.Equal("EXAMPLE.com.", Parse(HelloBuilder.Hello("EXAMPLE.com.")).ServerName);
    }

    [Fact]
    public void A_hello_without_the_extension_is_missing_sni()
    {
        Assert.Equal(TlsHelloStatus.MissingSni, Parse(HelloBuilder.Hello()).Status);
        var other = HelloBuilder.Record(HelloBuilder.Handshake(HelloBuilder.Extension(0x0a, [0, 2, 0, 0x1d])));
        Assert.Equal(TlsHelloStatus.MissingSni, Parse(other).Status);
    }

    [Fact]
    public void Two_names_in_one_extension_are_malformed()
    {
        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Hello("example.com", "evil.test")).Status);
    }

    [Fact]
    public void Two_server_name_extensions_are_malformed()
    {
        var hello = HelloBuilder.Record(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"), HelloBuilder.ServerNameExtension("evil.test")));

        Assert.Equal(TlsHelloStatus.Malformed, Parse(hello).Status);
    }

    [Fact]
    public void A_zero_length_name_is_malformed()
    {
        var ext = HelloBuilder.Extension(0, [0, 3, 0, 0, 0]);

        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Record(HelloBuilder.Handshake(ext))).Status);
    }

    [Fact]
    public void A_non_host_name_type_is_malformed()
    {
        var ext = HelloBuilder.Extension(0, [0, 4, 1, 0, 1, (byte)'a']);

        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Record(HelloBuilder.Handshake(ext))).Status);
    }

    [Fact]
    public void A_non_ascii_or_control_name_is_malformed()
    {
        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Hello("bücher.example")).Status);
        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Hello("exa mple.com")).Status);
        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Hello("example.com\0evil.test")).Status);
    }

    [Fact]
    public void An_idn_name_in_a_labels_is_returned()
    {
        Assert.Equal("xn--bcher-kva.example", Parse(HelloBuilder.Hello("xn--bcher-kva.example")).ServerName);
    }

    [Theory]
    [InlineData(0xfe0d)]
    [InlineData(0xffce)]
    public void Encrypted_hello_extensions_are_refused(int type)
    {
        var hello = HelloBuilder.Record(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"), HelloBuilder.Extension(type, [0, 1, 2, 3])));

        Assert.Equal(TlsHelloStatus.Ech, Parse(hello).Status);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void A_hello_split_over_several_records_is_reassembled(int parts)
    {
        var handshake = HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"), HelloBuilder.Extension(0x15, new byte[40]));
        var wire = HelloBuilder.Split(handshake, parts);

        var r = Parse(wire);

        Assert.Equal(TlsHelloStatus.Ok, r.Status);
        Assert.Equal("example.com", r.ServerName);
    }

    [Fact]
    public void Every_truncation_of_a_valid_hello_needs_more_or_is_decided_never_ok_early()
    {
        var wire = HelloBuilder.Split(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com")), 3);
        for (var cut = 0; cut < wire.Length; cut++)
        {
            Assert.Equal(TlsHelloStatus.NeedMore, TlsClientHello.Parse(wire.AsSpan(0, cut)).Status);
        }

        Assert.Equal(TlsHelloStatus.Ok, TlsClientHello.Parse(wire).Status);
    }

    [Fact]
    public void A_name_length_beyond_the_extension_is_malformed()
    {
        // server_name list says 100 bytes of name, extension body holds 3.
        var ext = HelloBuilder.Extension(0, [0, 6, 0, 0, 100, (byte)'a', (byte)'b', (byte)'c']);

        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Record(HelloBuilder.Handshake(ext))).Status);
    }

    [Fact]
    public void An_extension_length_beyond_the_record_is_malformed()
    {
        var hs = HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"));
        // Raise the 2-byte length of the first extension (its length sits just before the list) by a lot.
        var at = hs.Length - 18; // [ext len 2][list len 2][type 1][name len 2][name 11]: the high byte of the extension length
        hs[at] = 0x7f;

        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Record(hs)).Status);
    }

    [Fact]
    public void An_extensions_block_that_lies_about_its_length_is_malformed()
    {
        var hs = HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"));
        var extLenAt = hs.Length - 22; // the 2-byte length of the whole extensions block, just before the extension header
        hs[extLenAt + 1]++; // one more than is present

        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Record(hs)).Status);
    }

    [Fact]
    public void A_record_that_is_not_a_client_hello_is_malformed()
    {
        var hs = HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"));
        hs[0] = 2; // ServerHello

        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Record(hs)).Status);
    }

    [Fact]
    public void Extra_handshake_bytes_after_the_hello_are_malformed()
    {
        byte[] hs = [.. HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com")), 0, 0, 0, 0];

        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Record(hs)).Status);
    }

    [Fact]
    public void A_continuation_record_of_another_type_is_malformed()
    {
        var wire = HelloBuilder.Split(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com")), 2);
        var secondRecordAt = 5 + (wire.Length - 10) / 2 + ((wire.Length - 10) % 2);
        wire[secondRecordAt] = 0x17; // application data in the middle of the hello

        Assert.Equal(TlsHelloStatus.Malformed, Parse(wire).Status);
    }

    [Fact]
    public void An_empty_record_in_the_middle_is_malformed()
    {
        var hs = HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"));
        byte[] wire = [0x16, 3, 1, 0, 0, .. HelloBuilder.Record(hs)];

        Assert.Equal(TlsHelloStatus.Malformed, Parse(wire).Status);
    }

    [Fact]
    public void A_hello_that_never_completes_within_the_cap_is_malformed()
    {
        // A handshake that claims to be 12000 bytes but is fed as 1-byte records would need more than the cap.
        var wire = new List<byte>();
        wire.AddRange(HelloBuilder.Record([1, 0, 0x2e, 0xe0]));
        while (wire.Count < TlsClientHello.MaxBytes)
        {
            wire.AddRange(HelloBuilder.Record([0]));
        }

        Assert.Equal(TlsHelloStatus.Malformed, TlsClientHello.Parse(wire.ToArray()).Status);
    }

    [Fact]
    public void A_declared_hello_larger_than_the_cap_is_malformed()
    {
        Assert.Equal(TlsHelloStatus.Malformed, Parse(HelloBuilder.Record([1, 0xff, 0xff, 0xff])).Status);
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x2e, 0x01, 0x03, 0x01 })]      // SSLv2-style hello: not a TLS record here, relayed unchanged
    [InlineData(new byte[] { 0x16, 2, 0 })]                        // major version 2
    [InlineData(new byte[] { 0x16, 4, 0 })]
    [InlineData(new byte[] { 0x17, 3, 3, 0, 1, 0 })]               // application data
    [InlineData(new byte[] { (byte)'G', (byte)'E', (byte)'T' })]
    [InlineData(new byte[] { (byte)'S', (byte)'S', (byte)'H', (byte)'-' })]
    public void Non_tls_first_bytes_are_not_tls(byte[] bytes)
    {
        Assert.Equal(TlsHelloStatus.NotTls, Parse(bytes).Status);
    }

    [Fact]
    public void Empty_input_needs_more()
    {
        Assert.Equal(TlsHelloStatus.NeedMore, Parse([]).Status);
        Assert.Equal(TlsHelloStatus.NeedMore, Parse([0x16]).Status);
    }

    [Fact]
    public void Random_mutations_and_truncations_never_throw()
    {
        var seeds = new[]
        {
            HelloBuilder.Hello("example.com"),
            HelloBuilder.Split(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"), HelloBuilder.Extension(0x0a, [0, 2, 0, 0x1d])), 3),
            HelloBuilder.Record(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("a.test"), HelloBuilder.Extension(0xfe0d, new byte[20]))),
        };
        var rng = new Random(20261119);
        var counts = new Dictionary<TlsHelloStatus, int>();
        for (var i = 0; i < 20_000; i++)
        {
            var bytes = (byte[])seeds[rng.Next(seeds.Length)].Clone();
            var flips = rng.Next(0, 6);
            for (var f = 0; f < flips; f++)
            {
                bytes[rng.Next(bytes.Length)] = rng.Next(4) == 0 ? (byte)rng.Next(256) : (byte)(bytes[rng.Next(bytes.Length)] ^ (1 << rng.Next(8)));
            }

            var len = rng.Next(3) == 0 ? rng.Next(bytes.Length + 1) : bytes.Length;
            if (rng.Next(10) == 0)
            {
                var junk = new byte[rng.Next(0, 64)];
                rng.NextBytes(junk);
                bytes = [.. bytes.AsSpan(0, len), .. junk];
                len = bytes.Length;
            }

            var result = TlsClientHello.Parse(bytes.AsSpan(0, len));
            counts[result.Status] = counts.GetValueOrDefault(result.Status) + 1;
            if (result.Status == TlsHelloStatus.Ok)
            {
                Assert.False(string.IsNullOrEmpty(result.ServerName));
            }
        }

        // The corpus must actually exercise more than one outcome, or the test proves nothing.
        Assert.True(counts.Count >= 4, string.Join(",", counts));
    }

    [Fact]
    public void Pure_random_bytes_never_throw()
    {
        var rng = new Random(7);
        for (var i = 0; i < 5_000; i++)
        {
            var bytes = new byte[rng.Next(0, 300)];
            rng.NextBytes(bytes);
            if (bytes.Length > 2 && rng.Next(2) == 0)
            {
                bytes[0] = 0x16;
                bytes[1] = 3;
            }

            _ = TlsClientHello.Parse(bytes);
        }
    }
}
