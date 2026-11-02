using Bazaar.Server.Security.Readers;

namespace Bazaar.Server.Tests.Security.Readers;

public class HandoverSchemaTests
{
    [Fact]
    public void Create_keeps_declared_fields_in_order()
    {
        var schema = ReaderTestData.SampleSchema;

        Assert.Equal("test.sample", schema.Id);
        var expected = new[] { "subject", "priority", "tags", "score", "urgent", "link" };
        Assert.Equal(expected, schema.Fields.Select(f => f.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad-Id")]
    [InlineData("1starts_with_digit")]
    [InlineData("has space")]
    public void Create_rejects_malformed_schema_ids(string id) =>
        Assert.Throws<ArgumentException>(() => HandoverSchema.Create(id, new BoolField("flag")));

    [Theory]
    [InlineData("Subject")]
    [InlineData("has-dash")]
    [InlineData("9lives")]
    public void Create_rejects_malformed_field_names(string name) =>
        Assert.Throws<ArgumentException>(() => HandoverSchema.Create("test.names", new BoolField(name)));

    [Fact]
    public void Create_rejects_a_repeated_field_name() =>
        Assert.Throws<ArgumentException>(() => HandoverSchema.Create("test.dup", new BoolField("flag"), new StringField("flag", 5)));

    [Fact]
    public void Create_rejects_zero_length_limits() =>
        Assert.Throws<ArgumentException>(() => HandoverSchema.Create("test.limits", new StringField("text", 0)));

    [Fact]
    public void Create_rejects_a_zero_item_array_limit() =>
        Assert.Throws<ArgumentException>(() => HandoverSchema.Create("test.limits", new StringListField("tags", 0, 5)));

    [Fact]
    public void Create_rejects_an_integer_range_with_minimum_above_maximum() =>
        Assert.Throws<ArgumentException>(() => HandoverSchema.Create("test.range", new IntegerField("score", 5, 1)));

    [Fact]
    public void Create_rejects_an_empty_enum()
    {
        Assert.Throws<ArgumentException>(() => HandoverSchema.Create("test.enum", new EnumField("choice", [])));
    }

    [Fact]
    public void Create_rejects_a_duplicate_enum_value() =>
        Assert.Throws<ArgumentException>(() => HandoverSchema.Create("test.enum", new EnumField("choice", ["a", "a"])));

    [Fact]
    public void Registry_returns_registered_schema_and_refuses_unknown_ids()
    {
        var registry = new HandoverSchemaRegistry.Builder().Register(ReaderTestData.SampleSchema).Build();

        Assert.True(registry.TryGet("test.sample", out var found));
        Assert.Same(ReaderTestData.SampleSchema, found);
        Assert.False(registry.TryGet("test.other", out _));
        Assert.False(HandoverSchemaRegistry.Empty.TryGet("test.sample", out _));
    }

    [Fact]
    public void Registry_refuses_a_schema_registered_twice() =>
        Assert.Throws<ArgumentException>(() => new HandoverSchemaRegistry.Builder()
            .Register(ReaderTestData.SampleSchema)
            .Register(ReaderTestData.SampleSchema));
}
