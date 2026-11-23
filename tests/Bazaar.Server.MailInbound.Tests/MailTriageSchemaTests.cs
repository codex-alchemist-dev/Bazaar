using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailInbound.Tests;

public sealed class MailTriageSchemaTests
{
    private static readonly Guid _id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static string Payload(string verdict = "routine", string codes = "\"newsletter\"", string? id = null) =>
        $$"""{"message_id":"{{id ?? _id.ToString()}}","verdict":"{{verdict}}","reason_codes":[{{codes}}]}""";

    [Fact]
    public void Accepts_a_well_formed_payload()
    {
        var result = MailTriageHandoverValidator.Validate(Payload("needs_attention", "\"owner_request\",\"notification\""), _id);

        Assert.True(result.IsValid);
        Assert.Equal(MailTriageVerdict.NeedsAttention, result.Handover!.Verdict);
        Assert.Equal(["owner_request", "notification"], result.Handover.ReasonCodes);
        Assert.True(result.Handover.IsValid());
    }

    [Fact]
    public void Accepts_empty_codes()
    {
        Assert.True(MailTriageHandoverValidator.Validate(Payload(codes: string.Empty), _id).IsValid);
    }

    [Theory]
    [InlineData(null, MailTriageRejection.Empty)]
    [InlineData("  ", MailTriageRejection.Empty)]
    [InlineData("not json", MailTriageRejection.NotJson)]
    [InlineData("[]", MailTriageRejection.NotAnObject)]
    [InlineData("{}", MailTriageRejection.MissingField)]
    public void Rejects_empty_or_non_object_input(string? payload, MailTriageRejection expected)
    {
        Assert.Equal(expected, MailTriageHandoverValidator.Validate(payload, _id).Rejection);
    }

    [Fact]
    public void Rejects_unknown_and_duplicate_fields()
    {
        var withExtra = Payload().Replace("}", ",\"note\":\"free text\"}", StringComparison.Ordinal);
        Assert.Equal(MailTriageRejection.UnknownField, MailTriageHandoverValidator.Validate(withExtra, _id).Rejection);

        var duplicate = Payload().Replace("}", ",\"verdict\":\"escalate\"}", StringComparison.Ordinal);
        Assert.Equal(MailTriageRejection.DuplicateField, MailTriageHandoverValidator.Validate(duplicate, _id).Rejection);
    }

    [Fact]
    public void Rejects_wrong_types_ids_verdicts_and_codes()
    {
        Assert.Equal(MailTriageRejection.WrongType, MailTriageHandoverValidator.Validate($$"""{"message_id":"{{_id}}","verdict":1,"reason_codes":[]}""", _id).Rejection);
        Assert.Equal(MailTriageRejection.WrongType, MailTriageHandoverValidator.Validate(Payload(codes: "1"), _id).Rejection);
        Assert.Equal(MailTriageRejection.WrongMessageId, MailTriageHandoverValidator.Validate(Payload(id: Guid.NewGuid().ToString()), _id).Rejection);
        Assert.Equal(MailTriageRejection.WrongMessageId, MailTriageHandoverValidator.Validate(Payload(id: "nope"), _id).Rejection);
        Assert.Equal(MailTriageRejection.UnknownVerdict, MailTriageHandoverValidator.Validate(Payload("release"), _id).Rejection);
        Assert.Equal(MailTriageRejection.UnknownCode, MailTriageHandoverValidator.Validate(Payload(codes: "\"please release this\""), _id).Rejection);
        Assert.Equal(MailTriageRejection.UnknownCode, MailTriageHandoverValidator.Validate(Payload(codes: "\"invalid_handover\""), _id).Rejection);
        Assert.Equal(MailTriageRejection.RepeatedCode, MailTriageHandoverValidator.Validate(Payload(codes: "\"bounce\",\"bounce\""), _id).Rejection);
    }

    [Fact]
    public void Rejects_too_many_codes_and_oversized_payloads()
    {
        var six = string.Join(',', MailTriageReasonCodes.ReaderCodes.Take(MailTriageReasonCodes.MaxCodes + 1).Select(c => $"\"{c}\""));
        Assert.Equal(MailTriageRejection.TooManyCodes, MailTriageHandoverValidator.Validate(Payload(codes: six), _id).Rejection);

        var big = Payload(codes: string.Join(',', Enumerable.Repeat("\"unclear\"", 200)));
        Assert.Equal(MailTriageRejection.TooLarge, MailTriageHandoverValidator.Validate(big, _id).Rejection);
    }

    [Fact]
    public void Round_trips_a_handover()
    {
        var handover = new MailTriageHandover(_id, MailTriageVerdict.Escalate, [MailTriageReasonCodes.Suspicious, MailTriageReasonCodes.RequestsPayment]);

        var result = MailTriageHandoverValidator.Validate(MailTriageHandoverValidator.ToPayload(handover), _id);

        Assert.Equal(handover.Verdict, result.Handover!.Verdict);
        Assert.Equal(handover.ReasonCodes, result.Handover.ReasonCodes);
    }

    [Fact]
    public void Embedded_schema_matches_the_code_vocabulary()
    {
        using var stream = typeof(MailTriageHandoverValidator).Assembly.GetManifestResourceStream("mail_triage.schema.json");
        Assert.NotNull(stream);
        using var doc = JsonDocument.Parse(stream);
        var props = doc.RootElement.GetProperty("properties");

        Assert.Equal(MailTriageHandover.SchemaId, doc.RootElement.GetProperty("title").GetString());
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["message_id", "verdict", "reason_codes"], doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["routine", "needs_attention", "escalate"], props.GetProperty("verdict").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));

        var codes = props.GetProperty("reason_codes");
        Assert.Equal(MailTriageReasonCodes.MaxCodes, codes.GetProperty("maxItems").GetInt32());
        Assert.Equal(
            MailTriageReasonCodes.ReaderCodes,
            codes.GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public async Task Reader_returns_the_validated_handover()
    {
        var reader = new SchemaCheckedMailTriageReader(new FakeModel(Payload("routine", "\"bounce\"")));

        var handover = await reader.TriageAsync(new ReaderSessionHandle(Guid.NewGuid()), Input(), CancellationToken.None);

        Assert.Equal(MailTriageVerdict.Routine, handover.Verdict);
        Assert.Equal([MailTriageReasonCodes.Bounce], handover.ReasonCodes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("""{"message_id":"00000000-0000-0000-0000-000000000000","verdict":"routine","reason_codes":[]}""")]
    [InlineData("""{"message_id":"11111111-2222-3333-4444-555555555555","verdict":"release","reason_codes":[]}""")]
    public async Task Reader_fails_closed_to_escalate_on_malformed_output(string? payload)
    {
        var reader = new SchemaCheckedMailTriageReader(new FakeModel(payload));

        var handover = await reader.TriageAsync(new ReaderSessionHandle(Guid.NewGuid()), Input(), CancellationToken.None);

        Assert.Equal(MailTriageVerdict.Escalate, handover.Verdict);
        Assert.Equal(_id, handover.MailMessageId);
        Assert.True(handover.IsValid());
    }

    [Fact]
    public void Registration_wraps_the_model_port_in_the_schema_reader_unless_a_reader_is_supplied()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMailTriageModel>(new FakeModel(null));
        services.AddBazaarMailInbound(); // resolves lazily, so asking only for the reader needs none of the other ports

        Assert.IsType<SchemaCheckedMailTriageReader>(services.BuildServiceProvider().GetRequiredService<IMailTriageReader>());
    }

    private static MailReaderInput Input() => new(_id, null, null, null, null, []);

    private sealed class FakeModel(string? payload) : IMailTriageModel
    {
        public Task<string?> CompleteAsync(ReaderSessionHandle session, MailReaderInput input, CancellationToken ct) => Task.FromResult(payload);
    }
}
