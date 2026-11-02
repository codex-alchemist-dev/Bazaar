using System.Collections.Immutable;
using System.Text.Json;
using Bazaar.Server.Security.Readers;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Readers;

public class HandoverValidatorTests
{
    private static readonly HandoverValidator _validator = ReaderTestData.Validator();

    private static HandoverResult Validate(string json) => _validator.Validate("test.sample", ReaderTestData.Parse(json));

    private static HandoverRejectCode RejectCode(string json)
    {
        var result = Validate(json);
        var rejected = Assert.IsType<HandoverResult.Rejected>(result);
        return rejected.Code;
    }

    [Theory]
    [InlineData("""{"subject":"Hello","priority":"low","score":1}""")]
    [InlineData("""{"subject":"Hello","priority":"high","score":10,"tags":["a","b","c"],"urgent":true,"link":"https://example.com/x"}""")]
    [InlineData("""{"subject":"Hello","priority":"medium","score":0,"urgent":false,"link":"http://example.com"}""")]
    public void Valid_payloads_are_accepted(string json) =>
        Assert.IsType<HandoverResult.Accepted>(Validate(json));

    [Theory]
    [InlineData("""{"priority":"low","score":1}""", HandoverRejectCode.MissingField)]
    [InlineData("""{"subject":"Hello","score":1}""", HandoverRejectCode.MissingField)]
    [InlineData("""{"subject":"Hello","priority":"low"}""", HandoverRejectCode.MissingField)]
    [InlineData("""{"subject":5,"priority":"low","score":1}""", HandoverRejectCode.WrongType)]
    [InlineData("""{"subject":null,"priority":"low","score":1}""", HandoverRejectCode.WrongType)]
    [InlineData("""{"subject":"Hello","priority":"low","score":"7"}""", HandoverRejectCode.WrongType)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"urgent":"yes"}""", HandoverRejectCode.WrongType)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"tags":"a"}""", HandoverRejectCode.WrongType)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"tags":[1]}""", HandoverRejectCode.WrongType)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"link":5}""", HandoverRejectCode.WrongType)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"extra":"x"}""", HandoverRejectCode.UnknownField)]
    [InlineData("""{"subject":"Hello","subject":"Again","priority":"low","score":1}""", HandoverRejectCode.DuplicateField)]
    [InlineData("""{"subject":"Hello","priority":"urgent","score":1}""", HandoverRejectCode.BadEnumValue)]
    [InlineData("""{"subject":"Hello","priority":"High","score":1}""", HandoverRejectCode.BadEnumValue)]
    [InlineData("""{"subject":"Hello","priority":"low","score":11}""", HandoverRejectCode.OutOfRange)]
    [InlineData("""{"subject":"Hello","priority":"low","score":-1}""", HandoverRejectCode.OutOfRange)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1.5}""", HandoverRejectCode.OutOfRange)]
    [InlineData("""{"subject":"Hello","priority":"low","score":99999999999999999999}""", HandoverRejectCode.OutOfRange)]
    [InlineData("""{"subject":"abcdefghijklmnopqrstu","priority":"low","score":1}""", HandoverRejectCode.TooLong)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"tags":["a","b","c","d"]}""", HandoverRejectCode.TooManyItems)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"tags":["abcdefghijk"]}""", HandoverRejectCode.TooLong)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"link":"javascript:alert(1)"}""", HandoverRejectCode.BadUrl)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"link":"ftp://example.com/file"}""", HandoverRejectCode.BadUrl)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"link":"relative/path"}""", HandoverRejectCode.BadUrl)]
    [InlineData("""{"subject":"Hello","priority":"low","score":1,"link":"https://user:secret@example.com"}""", HandoverRejectCode.BadUrl)]
    public void Invalid_payloads_are_rejected_with_a_reason(string json, HandoverRejectCode expected) =>
        Assert.Equal(expected, RejectCode(json));

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Non_object_payloads_are_rejected(string json)
    {
        var result = _validator.Validate("test.sample", ReaderTestData.Parse(json));

        Assert.Equal(HandoverRejectCode.WrongType, Assert.IsType<HandoverResult.Rejected>(result).Code);
    }

    [Fact]
    public void Unknown_schema_is_rejected()
    {
        var result = _validator.Validate("test.missing", ReaderTestData.Parse(ReaderTestData.MinimalJson));

        Assert.Equal(HandoverRejectCode.UnknownSchema, Assert.IsType<HandoverResult.Rejected>(result).Code);
    }

    [Fact]
    public void Length_counts_code_points_not_utf16_units()
    {
        // Twenty emoji are 40 UTF-16 units but twenty code points, so they fit a 20-character limit.
        var twentyEmoji = string.Concat(Enumerable.Repeat("\U0001F600", 20));
        var accepted = Validate($$"""{"subject":"{{twentyEmoji}}","priority":"low","score":1}""");
        Assert.IsType<HandoverResult.Accepted>(accepted);

        var twentyOneEmoji = twentyEmoji + "\U0001F600";
        Assert.Equal(HandoverRejectCode.TooLong, RejectCode($$"""{"subject":"{{twentyOneEmoji}}","priority":"low","score":1}"""));
    }

    [Fact]
    public void Payload_over_64_KB_is_rejected_before_any_field_is_read()
    {
        var padding = new string('a', 70 * 1024);
        var json = $$"""{"subject":"Hello","priority":"low","score":1,"pad":"{{padding}}"}""";

        Assert.Equal(HandoverRejectCode.TooLarge, RejectCode(json));
    }

    [Fact]
    public void Nesting_deeper_than_eight_levels_is_rejected()
    {
        // The root object is level 1, so these arrays reach level 9.
        var json = """{"subject":"Hello","priority":"low","score":1,"extra":""" + new string('[', 8) + new string(']', 8) + "}";

        Assert.Equal(HandoverRejectCode.TooDeep, RejectCode(json));
    }

    [Fact]
    public void Nesting_of_exactly_eight_levels_passes_the_depth_gate()
    {
        // Eight levels: the root object plus seven arrays. The unknown field is then refused on its own.
        var json = """{"subject":"Hello","priority":"low","score":1,"extra":""" + new string('[', 7) + new string(']', 7) + "}";

        Assert.Equal(HandoverRejectCode.UnknownField, RejectCode(json));
    }

    [Fact]
    public void Accepted_output_holds_only_declared_fields_with_agent_provenance()
    {
        var result = Validate("""{"subject":"Hello","priority":"high","score":4,"tags":["x"],"urgent":true}""");

        var accepted = Assert.IsType<HandoverResult.Accepted>(result).Handover;
        Assert.Equal("test.sample", accepted.SchemaId);
        var expected = new[] { "priority", "score", "subject", "tags", "urgent" };
        Assert.Equal(expected, accepted.Fields.Keys.Order());
        Assert.Equal(new TextValue("Hello"), accepted.Fields["subject"]);
        Assert.Equal(new IntegerValue(4), accepted.Fields["score"]);
        Assert.Equal(new BoolValue(true), accepted.Fields["urgent"]);
        var tags = new[] { "x" };
        Assert.Equal(tags, Assert.IsType<TextListValue>(accepted.Fields["tags"]).Items.ToArray());
        Assert.Equal(ContentTrust.Agent, accepted.Trust);
        Assert.Empty(accepted.TaintSources);
        Assert.Empty(accepted.DerivedFrom);
    }

    [Fact]
    public void Optional_fields_that_are_absent_are_not_in_the_output()
    {
        var accepted = Assert.IsType<HandoverResult.Accepted>(Validate(ReaderTestData.MinimalJson)).Handover;

        var expected = new[] { "priority", "score", "subject" };

        Assert.Equal(expected, accepted.Fields.Keys.Order());
    }

    [Fact]
    public void Lineage_is_recorded_only_when_stamped()
    {
        var accepted = Assert.IsType<HandoverResult.Accepted>(Validate(ReaderTestData.MinimalJson)).Handover;
        var source = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");

        var stamped = accepted.WithLineage([source]);

        var expected = new[] { source };

        Assert.Equal(expected, stamped.DerivedFrom.ToArray());
        Assert.Empty(accepted.DerivedFrom);
        Assert.Empty(stamped.TaintSources);
        Assert.Throws<ArgumentException>(() => accepted.WithLineage(ImmutableArray<Guid>.Empty));
    }

    [Fact]
    public void Same_payload_gives_the_same_rejection()
    {
        var json = """{"subject":"Hello","priority":"nope","score":1}""";

        Assert.Equal(Validate(json), Validate(json));
    }
}
