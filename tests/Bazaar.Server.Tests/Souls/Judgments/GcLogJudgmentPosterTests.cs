using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Souls.Judgments;

namespace Bazaar.Server.Tests.Souls.Judgments;

/// <summary>The GC-backed judgment poster over a fake log writer (docs/design/01 message write path).</summary>
public sealed class GcLogJudgmentPosterTests
{
    [Fact]
    public async Task A_judgment_is_appended_as_an_agent_message_keyed_by_its_decision_id()
    {
        var log = new FakeGcLogWriter();
        var poster = new GcLogJudgmentPoster(log, new PlaintextGcBodyCodec());
        var decisionId = JudgmentFixtures.Record().DecisionId;

        var receipt = await poster.PostAsync(
            new JudgmentPost(JudgmentFixtures.GcId, JudgmentFixtures.AgentId, decisionId, "Chose promote."),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(log.Requests);
        Assert.Equal(JudgmentFixtures.GcId, request.GcId);
        Assert.Equal(GcEventKinds.Message, request.Kind);
        Assert.Equal(AuthorKind.Agent, request.AuthorKind);
        Assert.Equal(JudgmentFixtures.AgentId, request.AuthorId);
        Assert.Equal(SourceTrust.Agent, request.SourceTrust);
        Assert.Equal($"judgment:{decisionId:D}", request.ClientMsgId);
        Assert.Equal(1, receipt.Seq);
        Assert.False(receipt.Duplicate);
    }

    [Fact]
    public async Task The_body_is_sealed_by_the_codec_and_never_sent_as_plaintext()
    {
        var log = new FakeGcLogWriter();
        var codec = new PlaintextGcBodyCodec();
        var poster = new GcLogJudgmentPoster(log, codec);

        await poster.PostAsync(
            new JudgmentPost(JudgmentFixtures.GcId, JudgmentFixtures.AgentId, Guid.NewGuid(), "Chose promote."),
            TestContext.Current.CancellationToken);

        var body = Assert.Single(log.Requests).Message;
        Assert.NotNull(body);
        Assert.Equal(PlaintextGcBodyCodec.PlaintextKeyId, body.KeyId);
        Assert.Equal("Chose promote.", codec.Open(body.BodyCiphertext, body.KeyId, body.Nonce));
    }

    [Fact]
    public async Task A_repeat_post_reports_the_original_event_as_a_duplicate()
    {
        var log = new FakeGcLogWriter();
        var poster = new GcLogJudgmentPoster(log, new PlaintextGcBodyCodec());
        var decisionId = Guid.NewGuid();
        var post = new JudgmentPost(JudgmentFixtures.GcId, JudgmentFixtures.AgentId, decisionId, "Chose promote.");

        var first = await poster.PostAsync(post, TestContext.Current.CancellationToken);
        var repeat = await poster.PostAsync(post, TestContext.Current.CancellationToken);

        Assert.Equal(log.Requests[0].ClientMsgId, log.Requests[1].ClientMsgId);
        Assert.False(first.Duplicate);
        Assert.True(repeat.Duplicate);
        Assert.Equal(first.Seq, repeat.Seq);
    }

    [Fact]
    public async Task A_null_post_is_refused()
    {
        var poster = new GcLogJudgmentPoster(new FakeGcLogWriter(), new PlaintextGcBodyCodec());

        await Assert.ThrowsAsync<ArgumentNullException>(() => poster.PostAsync(null!, TestContext.Current.CancellationToken));
    }
}
