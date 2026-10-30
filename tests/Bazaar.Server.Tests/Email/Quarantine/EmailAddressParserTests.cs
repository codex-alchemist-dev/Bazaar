using Bazaar.Server.Email.Quarantine;

namespace Bazaar.Server.Tests.Email.Quarantine;

public class EmailAddressParserTests
{
    [Theory]
    [InlineData("alice@example.org", "alice", "example.org")]
    [InlineData("  bob.smith+tag@sub.example.co.uk  ", "bob.smith+tag", "sub.example.co.uk")]
    [InlineData("o'neil@example.org", "o'neil", "example.org")]
    [InlineData("\"john doe\"@example.org", "\"john doe\"", "example.org")]
    [InlineData("\"a@b\"@example.org", "\"a@b\"", "example.org")]
    [InlineData("user@[192.168.0.1]", "user", "[192.168.0.1]")]
    [InlineData("user@[IPv6:2001:db8::1]", "user", "[IPv6:2001:db8::1]")]
    [InlineData("a-b@xn--bcher-kva.example", "a-b", "xn--bcher-kva.example")]
    public void Valid_addresses_parse(string text, string local, string domain)
    {
        Assert.True(EmailAddressParser.TryParse(text, out var address));
        Assert.Equal(local, address!.LocalPart);
        Assert.Equal(domain, address.Domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("no-at-sign.example.org")]
    [InlineData("@example.org")]
    [InlineData("alice@")]
    [InlineData(".alice@example.org")]
    [InlineData("alice.@example.org")]
    [InlineData("al..ice@example.org")]
    [InlineData("al ice@example.org")]
    [InlineData("alice@localhost")]
    [InlineData("alice@-example.org")]
    [InlineData("alice@example-.org")]
    [InlineData("alice@exa_mple.org")]
    [InlineData("alice@example..org")]
    [InlineData("alice@example.123")]
    [InlineData("alice@[1.2.3]")]
    [InlineData("alice@[300.1.1.1]")]
    [InlineData("alice@[1.2.3.4")]
    [InlineData("\"unterminated@example.org")]
    [InlineData("alice@example.org, bob@example.org")]
    public void Invalid_addresses_are_rejected(string? text)
    {
        Assert.False(EmailAddressParser.TryParse(text, out var address));
        Assert.Null(address);
    }

    [Fact]
    public void Local_part_longer_than_64_characters_is_rejected()
    {
        var local = new string('a', 65);

        Assert.False(EmailAddressParser.TryParse(local + "@example.org", out _));
        Assert.True(EmailAddressParser.TryParse(new string('a', 64) + "@example.org", out _));
    }

    [Fact]
    public void Address_longer_than_254_characters_is_rejected()
    {
        var label = new string('a', 60);
        var domain = string.Join('.', label, label, label, label, label);

        Assert.False(EmailAddressParser.TryParse("a@" + domain, out _));
    }

    [Fact]
    public void Mixed_case_domain_is_normalised_to_lowercase()
    {
        Assert.True(EmailAddressParser.TryParse("Alice@Example.ORG", out var address));

        Assert.Equal("Alice", address!.LocalPart);
        Assert.Equal("example.org", address.Domain);
        Assert.Equal("Alice@example.org", address.Value);
    }

    [Fact]
    public void Comparison_key_ignores_case_in_both_parts()
    {
        Assert.True(EmailAddressParser.TryParse("Alice@Example.org", out var first));
        Assert.True(EmailAddressParser.TryParse("alice@example.ORG", out var second));

        Assert.Equal(first!.ComparisonKey, second!.ComparisonKey);
    }

    [Fact]
    public void Mailbox_with_display_name_and_angle_brackets_parses()
    {
        Assert.True(EmailAddressParser.TryParseMailbox("Alice Smith <alice@example.org>", out var mailbox));

        Assert.Equal("Alice Smith", mailbox!.DisplayName);
        Assert.Equal("alice@example.org", mailbox.Address.Value);
    }

    [Fact]
    public void Quoted_display_name_is_unquoted()
    {
        Assert.True(EmailAddressParser.TryParseMailbox("\"Smith, Alice\" <alice@example.org>", out var mailbox));

        Assert.Equal("Smith, Alice", mailbox!.DisplayName);
    }

    [Fact]
    public void Bare_mailbox_has_no_display_name()
    {
        Assert.True(EmailAddressParser.TryParseMailbox("alice@example.org", out var mailbox));

        Assert.Null(mailbox!.DisplayName);
        Assert.Equal("alice@example.org", mailbox.Address.Value);
    }

    [Theory]
    [InlineData("Alice <not-an-address>")]
    [InlineData("Alice <alice@example.org")]
    [InlineData("<>")]
    [InlineData("")]
    public void Malformed_mailbox_is_rejected(string header)
    {
        Assert.False(EmailAddressParser.TryParseMailbox(header, out var mailbox));
        Assert.Null(mailbox);
    }
}
