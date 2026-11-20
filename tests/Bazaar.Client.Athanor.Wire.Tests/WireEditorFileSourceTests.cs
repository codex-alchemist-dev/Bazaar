using System.Text;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Athanor.Wire;
using Bazaar.Contracts.Athanor;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Athanor.Wire.Tests;

public sealed class WireEditorFileSourceTests
{
    private static readonly EditorDocumentId _doc = new("vault", "notes/a.md");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers each request type from a queue of replies or exceptions and records what was sent.</summary>
    private sealed class FakeRequester : IWireRequester
    {
        public List<(string Type, object Payload)> Sent { get; } = [];

        public Queue<Func<object>> Answers { get; } = new();

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload!));
            cancellationToken.ThrowIfCancellationRequested();
            var answer = Answers.Dequeue()();
            return Task.FromResult((TResponse)answer);
        }

        public FakeRequester Reply(object reply)
        {
            Answers.Enqueue(() => reply);
            return this;
        }

        public FakeRequester Throw(Exception ex)
        {
            Answers.Enqueue(() => throw ex);
            return this;
        }

        public FakeRequester Fail(string code, string? message = null) =>
            Throw(new WireRequestException(new WireError(code, message, null)));
    }

    private static EditorOpenReply OpenReply(string? hint = "markdown", bool readOnly = false) =>
        new("hello", "rev1", hint, readOnly, 5);

    [Fact]
    public async Task Open_maps_the_reply_to_a_document_and_sends_open()
    {
        var requests = new FakeRequester().Reply(OpenReply());

        var doc = await new WireEditorFileSource(requests).OpenAsync(_doc, Ct);

        Assert.Equal(new EditorDocument(_doc, "hello", "rev1", "markdown", false), doc);
        var sent = Assert.Single(requests.Sent);
        Assert.Equal("editor.open", sent.Type);
        Assert.Equal(new EditorOpenRequest("vault", "notes/a.md"), sent.Payload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Open_defaults_the_language_when_the_hint_is_missing(string? hint)
    {
        var requests = new FakeRequester().Reply(OpenReply(hint));

        var doc = await new WireEditorFileSource(requests).OpenAsync(_doc, Ct);

        Assert.Equal("plaintext", doc!.LanguageId);
    }

    [Fact]
    public async Task Open_keeps_the_read_only_flag()
    {
        var requests = new FakeRequester().Reply(OpenReply(readOnly: true));

        var doc = await new WireEditorFileSource(requests).OpenAsync(_doc, Ct);

        Assert.True(doc!.ReadOnly);
    }

    [Fact]
    public async Task Open_answers_null_when_not_found()
    {
        var requests = new FakeRequester().Fail(EditorErrorCodes.NotFound);

        Assert.Null(await new WireEditorFileSource(requests).OpenAsync(_doc, Ct));
    }

    [Theory]
    [InlineData(EditorErrorCodes.Forbidden)]
    [InlineData(EditorErrorCodes.TooLarge)]
    [InlineData(EditorErrorCodes.Binary)]
    [InlineData(EditorErrorCodes.ReadOnlyWorkspace)]
    [InlineData(EditorErrorCodes.Unavailable)]
    [InlineData("rate_limited")]
    public async Task Open_throws_a_coded_exception_for_other_refusals(string code)
    {
        var requests = new FakeRequester().Fail(code, "no");

        var ex = await Assert.ThrowsAsync<EditorFileException>(() => new WireEditorFileSource(requests).OpenAsync(_doc, Ct));

        Assert.Equal(code, ex.Code);
        Assert.Contains("no", ex.Message);
    }

    [Fact]
    public async Task Open_maps_transport_failure_to_unavailable()
    {
        var requests = new FakeRequester().Throw(new IOException("down"));

        var ex = await Assert.ThrowsAsync<EditorFileException>(() => new WireEditorFileSource(requests).OpenAsync(_doc, Ct));

        Assert.Equal(EditorErrorCodes.Unavailable, ex.Code);
    }

    [Fact]
    public async Task Open_maps_a_malformed_payload_exception_to_unavailable()
    {
        var requests = new FakeRequester().Throw(new WireFormatException("bad"));

        var ex = await Assert.ThrowsAsync<EditorFileException>(() => new WireEditorFileSource(requests).OpenAsync(_doc, Ct));

        Assert.Equal(EditorErrorCodes.Unavailable, ex.Code);
    }

    [Theory]
    [InlineData(null, "rev1")]
    [InlineData("text", null)]
    [InlineData("text", "")]
    public async Task Open_rejects_an_incomplete_reply(string? content, string? revision)
    {
        var reply = new EditorOpenReply(content!, revision!, null, false, 0);
        var requests = new FakeRequester().Reply(reply);

        var ex = await Assert.ThrowsAsync<EditorFileException>(() => new WireEditorFileSource(requests).OpenAsync(_doc, Ct));

        Assert.Equal(EditorErrorCodes.Malformed, ex.Code);
    }

    [Fact]
    public async Task Open_allows_empty_content()
    {
        var requests = new FakeRequester().Reply(new EditorOpenReply("", "rev1", null, false, 0));

        var doc = await new WireEditorFileSource(requests).OpenAsync(_doc, Ct);

        Assert.Equal("", doc!.Content);
    }

    [Fact]
    public async Task Open_lets_cancellation_through()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var requests = new FakeRequester().Reply(OpenReply());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WireEditorFileSource(requests).OpenAsync(_doc, cts.Token));
    }

    [Fact]
    public async Task Save_maps_an_accepted_save_and_sends_save()
    {
        var requests = new FakeRequester().Reply(new EditorSaveReply("rev2", null, null));

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "new", "rev1", Ct);

        Assert.Equal(new SaveOutcome.Saved("rev2"), outcome);
        var sent = Assert.Single(requests.Sent);
        Assert.Equal("editor.save", sent.Type);
        Assert.Equal(new EditorSaveRequest("vault", "notes/a.md", "rev1", "new"), sent.Payload);
    }

    [Fact]
    public async Task Save_maps_a_conflict_reply_carrying_the_server_copy()
    {
        var requests = new FakeRequester().Reply(new EditorSaveReply(null, "rev9", "server text"));

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "mine", "rev1", Ct);

        Assert.Equal(new SaveOutcome.Conflict("rev9", "server text"), outcome);
    }

    [Fact]
    public async Task Save_conflict_with_empty_server_content_is_still_a_conflict()
    {
        var requests = new FakeRequester().Reply(new EditorSaveReply(null, "rev9", ""));

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "mine", "rev1", Ct);

        Assert.Equal(new SaveOutcome.Conflict("rev9", ""), outcome);
    }

    [Fact]
    public async Task Save_conflict_error_fetches_the_server_copy_with_open()
    {
        var requests = new FakeRequester()
            .Fail(EditorErrorCodes.Conflict)
            .Reply(new EditorOpenReply("server text", "rev9", null, false, 11));

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "mine", "rev1", Ct);

        Assert.Equal(new SaveOutcome.Conflict("rev9", "server text"), outcome);
        Assert.Equal(["editor.save", "editor.open"], requests.Sent.Select(s => s.Type));
    }

    [Fact]
    public async Task Save_conflict_error_is_unavailable_when_the_copy_cannot_be_fetched()
    {
        var requests = new FakeRequester().Fail(EditorErrorCodes.Conflict).Fail(EditorErrorCodes.Unavailable);

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "mine", "rev1", Ct);

        Assert.IsType<SaveOutcome.Unavailable>(outcome);
    }

    [Fact]
    public async Task Save_conflict_error_is_unavailable_when_the_file_vanished()
    {
        var requests = new FakeRequester().Fail(EditorErrorCodes.Conflict).Fail(EditorErrorCodes.NotFound);

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "mine", "rev1", Ct);

        Assert.IsType<SaveOutcome.Unavailable>(outcome);
    }

    [Theory]
    [InlineData(EditorErrorCodes.NotFound)]
    [InlineData(EditorErrorCodes.Forbidden)]
    [InlineData(EditorErrorCodes.TooLarge)]
    [InlineData(EditorErrorCodes.Binary)]
    [InlineData(EditorErrorCodes.ReadOnlyWorkspace)]
    [InlineData(EditorErrorCodes.Unavailable)]
    [InlineData("rate_limited")]
    public async Task Save_maps_other_refusals_to_unavailable_with_the_code(string code)
    {
        var requests = new FakeRequester().Fail(code, "why");

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "x", "rev1", Ct);

        var unavailable = Assert.IsType<SaveOutcome.Unavailable>(outcome);
        Assert.Contains(code, unavailable.Reason);
        Assert.Contains("why", unavailable.Reason);
    }

    [Fact]
    public async Task Save_maps_transport_failure_to_unavailable()
    {
        var requests = new FakeRequester().Throw(new HttpRequestException("down"));

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "x", "rev1", Ct);

        Assert.IsType<SaveOutcome.Unavailable>(outcome);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData(null, "rev9", null)]
    [InlineData(null, null, "text")]
    [InlineData("rev2", "rev9", "text")]
    public async Task Save_treats_an_incomplete_or_contradictory_reply_as_unavailable(string? newRevision, string? serverRevision, string? serverContent)
    {
        var requests = new FakeRequester().Reply(new EditorSaveReply(newRevision, serverRevision, serverContent));

        var outcome = await new WireEditorFileSource(requests).SaveAsync(_doc, "x", "rev1", Ct);

        Assert.IsType<SaveOutcome.Unavailable>(outcome);
    }

    [Fact]
    public async Task Save_lets_cancellation_through()
    {
        var requests = new FakeRequester().Throw(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WireEditorFileSource(requests).SaveAsync(_doc, "x", "rev1", Ct));
    }

    [Theory]
    [InlineData("", "a.md")]
    [InlineData("vault", "")]
    [InlineData("vault", "/a.md")]
    [InlineData("vault", "a\\b.md")]
    [InlineData("vault", "../a.md")]
    [InlineData("vault", "a/./b.md")]
    [InlineData("vault", "a//b.md")]
    [InlineData("vault", "a/")]
    public async Task Unnormalised_ids_are_refused_before_anything_is_sent(string workspace, string path)
    {
        var requests = new FakeRequester();
        var source = new WireEditorFileSource(requests);
        var id = new EditorDocumentId(workspace, path);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => source.OpenAsync(id, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => source.SaveAsync(id, "x", "rev1", Ct));
        Assert.Empty(requests.Sent);
    }

    [Fact]
    public async Task Save_requires_a_base_revision()
    {
        var source = new WireEditorFileSource(new FakeRequester());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => source.SaveAsync(_doc, "x", "", Ct));
    }

    [Fact]
    public void Payloads_round_trip_through_the_real_json_wire_codec()
    {
        var codec = new JsonWireCodec();

        var open = codec.EncodePayload(new EditorOpenRequest("vault", "a.md"));
        Assert.Equal("{\"workspaceId\":\"vault\",\"path\":\"a.md\"}", Encoding.UTF8.GetString(open.Span));

        var env = new Envelope(1, WireTypes.EditorSave, EnvelopeKind.Response, "1", null, null, null, null,
            codec.EncodePayload(new EditorSaveReply(null, "rev9", "text")));
        Assert.Equal(new EditorSaveReply(null, "rev9", "text"), codec.DecodePayload<EditorSaveReply>(env));

        var openReply = new Envelope(1, WireTypes.EditorOpen, EnvelopeKind.Response, "2", null, null, null, null,
            Encoding.UTF8.GetBytes("{\"content\":\"x\",\"revision\":\"r\",\"readOnly\":true,\"sizeBytes\":1,\"extra\":1}"));
        Assert.Equal(new EditorOpenReply("x", "r", null, true, 1), codec.DecodePayload<EditorOpenReply>(openReply));
    }
}
