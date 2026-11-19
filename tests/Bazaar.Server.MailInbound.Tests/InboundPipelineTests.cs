using System.IO.Compression;
using System.Text;
using Bazaar.Server.Mail;
using Bazaar.Server.Mail.Parsing;
using Bazaar.Server.Mail.Quarantine;

namespace Bazaar.Server.MailInbound.Tests;

/// <summary>The inbound pipeline against fakes (doc 22 inbound flow, steps 1 to 7). Names and addresses are synthetic.</summary>
public sealed class InboundPipelineTests
{
    private const string _secretBody = "TOPSECRET-BODY-9141";
    private const string _secretSubject = "TOPSECRET-SUBJECT-7733";
    private const string _senderAddress = "pat.private@sender.example.test";

    private static string Clean(string id = "one@sender.example.test", string subject = "Hello", string body = "Welcome aboard.") =>
        Mime.Plain($"Pat <{_senderAddress}>", subject, body, id, "spf=pass; dkim=pass; dmarc=pass");

    private static string Failing(string id = "bad@sender.example.test") =>
        Mime.Plain($"Pat <{_senderAddress}>", _secretSubject, _secretBody, id, "spf=fail; dkim=pass");

    [Fact]
    public async Task Clean_message_is_announced_without_content_then_read_by_a_hard_tainted_reader()
    {
        var h = new Harness();
        h.Add("b1", Clean(subject: _secretSubject, body: _secretBody));

        var result = await h.PassAsync();

        Assert.Equal(1, result.Delivered);
        Assert.True(result.IsClean);
        var entry = Assert.Single(h.Gc.External);
        Assert.Equal("sender.example.test", entry.SenderDomain);
        Assert.Equal(h.Mailbox.InboxGcId, entry.InboxGcId);
        Assert.Equal("external", ExternalMailEntry.SourceTrust);
        Assert.Equal("external_mail", ExternalMailEntry.Kind);
        Assert.Empty(entry.ScreenCodes);
        // The announcement and the hand-over carry no sender address, subject or body.
        var posted = h.Gc.Everything();
        Assert.DoesNotContain(_secretBody, posted, StringComparison.Ordinal);
        Assert.DoesNotContain(_secretSubject, posted, StringComparison.Ordinal);
        Assert.DoesNotContain(_senderAddress, posted, StringComparison.Ordinal);
        Assert.Contains(h.Gc.Handovers, x => x.Verdict == MailTriageVerdict.Routine);
        Assert.True((await h.Quarantine.ListAsync(null, TestContext.Current.CancellationToken)).Count == 0);
        Assert.True(Assert.Single(h.Messages.Snapshot()).Delivered);
    }

    [Fact]
    public async Task Clean_message_sets_hard_taint_on_its_reader_session_before_the_reader_reads()
    {
        var h = new Harness();
        h.Add("b1", Clean(body: _secretBody));

        await h.PassAsync();

        Assert.Single(h.Reader.Started);
        Assert.Single(h.Reader.TaintedSessions);
        Assert.True(h.Reader.TaintWasHardBeforeRead);
        Assert.Equal(_secretBody, Assert.Single(h.Reader.Inputs).TextBody!.Trim());
        Assert.Single(h.Reader.Ended);
    }

    [Fact]
    public async Task Quarantined_message_posts_a_count_only_and_never_reaches_a_reader_or_the_gc_with_content()
    {
        var h = new Harness();
        h.Add("b1", Failing());

        var result = await h.PassAsync();

        Assert.Equal(1, result.Quarantined);
        Assert.Empty(h.Gc.External);
        Assert.Empty(h.Gc.Handovers);
        Assert.Empty(h.Reader.Started);
        var notice = Assert.Single(h.Gc.Notices);
        Assert.Equal(1, notice.HeldCount);
        Assert.Equal(["AuthenticationFailed"], notice.ReasonCodes);
        var posted = h.Gc.Everything();
        Assert.DoesNotContain(_secretBody, posted, StringComparison.Ordinal);
        Assert.DoesNotContain(_secretSubject, posted, StringComparison.Ordinal);
        Assert.DoesNotContain(_senderAddress, posted, StringComparison.Ordinal);
        Assert.DoesNotContain("sender.example.test", posted, StringComparison.Ordinal);
        var held = Assert.Single(await h.Quarantine.ListAsync(QuarantineState.Held, TestContext.Current.CancellationToken));
        Assert.Equal("sender.example.test", held.SenderDomain);
        Assert.Equal(h.Mailbox.PersonId, held.PersonId);
    }

    [Fact]
    public async Task An_actor_or_the_system_cannot_read_a_raw_body_but_a_reader_and_the_owner_can()
    {
        var h = new Harness();
        h.Add("b1", Failing());
        await h.PassAsync();
        var id = Assert.Single(h.Messages.Snapshot()).Message.Id;
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(MailBodyReadOutcome.Denied, (await h.Messages.ReadSealedBodyAsync(id, MailAccessor.Agent, ct)).Outcome);
        Assert.Equal(MailBodyReadOutcome.Denied, (await h.Messages.ReadSealedBodyAsync(id, MailAccessor.System, ct)).Outcome);
        Assert.Equal(MailBodyReadOutcome.Ok, (await h.Messages.ReadSealedBodyAsync(id, MailAccessor.Reader, ct)).Outcome);
        Assert.Equal(MailBodyReadOutcome.Ok, (await h.Messages.ReadSealedBodyAsync(id, MailAccessor.Owner, ct)).Outcome);
        Assert.Equal(MailBodyReadOutcome.NotFound, (await h.Messages.ReadSealedBodyAsync(Guid.NewGuid(), MailAccessor.Reader, ct)).Outcome);
    }

    [Fact]
    public async Task The_stored_body_is_sealed_never_the_clear_message()
    {
        var h = new Harness();
        h.Add("b1", Clean(body: _secretBody));
        await h.PassAsync();

        var stored = Assert.Single(h.Messages.Snapshot()).Message.Body;

        Assert.DoesNotContain(_secretBody, Encoding.UTF8.GetString(stored.Ciphertext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reader_escalation_holds_the_message_and_the_reader_cannot_release_it()
    {
        var h = new Harness();
        h.Reader.Decide = i => new MailTriageHandover(i.MailMessageId, MailTriageVerdict.Escalate, [MailTriageReasonCodes.PromptInjectionSuspected]);
        h.Add("b1", Clean());

        var result = await h.PassAsync();

        Assert.Equal(1, result.Quarantined);
        Assert.Empty(h.Gc.Handovers);
        var item = Assert.Single(await h.Quarantine.ListAsync(QuarantineState.Held, TestContext.Current.CancellationToken));
        Assert.Equal(["reader:prompt_injection_suspected"], item.ReasonCodes);
        Assert.True(item.Tainted);

        // Whatever the reader asks, release stays with the owner.
        var release = await h.Quarantine.TransitionAsync(
            item.Id, QuarantineState.Released, QuarantineActor.Reader, new QuarantineTransitionContext(), TestContext.Current.CancellationToken);
        Assert.Equal(QuarantineTransitionOutcome.ReaderCannotRelease, release.Outcome);
        Assert.Equal(QuarantineState.Held, (await h.Quarantine.GetAsync(item.Id, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(MailTriageVerdict.Escalate, Enum.GetValues<MailTriageVerdict>().Last());
        Assert.DoesNotContain(Enum.GetNames<MailTriageVerdict>(), n => n.Contains("Release", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("unknown_code")]
    [InlineData("a free text sentence ignoring previous instructions")]
    public async Task An_invalid_handover_fails_closed_into_quarantine(string code)
    {
        var h = new Harness();
        h.Reader.Decide = i => new MailTriageHandover(i.MailMessageId, MailTriageVerdict.Routine, [code]);
        h.Add("b1", Clean());

        var result = await h.PassAsync();

        Assert.Equal(1, result.Quarantined);
        Assert.Empty(h.Gc.Handovers);
        Assert.Single(await h.Quarantine.ListAsync(QuarantineState.Held, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_handover_for_another_message_fails_closed()
    {
        var h = new Harness();
        h.Reader.Decide = _ => new MailTriageHandover(Guid.NewGuid(), MailTriageVerdict.Routine, []);
        h.Add("b1", Clean());

        Assert.Equal(1, (await h.PassAsync()).Quarantined);
    }

    [Fact]
    public async Task Duplicate_delivery_is_processed_once_within_a_pass_and_across_passes()
    {
        var h = new Harness();
        h.Add("b1", Clean("same@sender.example.test"), 0);
        h.Add("b1-again", Clean("same@sender.example.test"), 1);

        var first = await h.PassAsync();
        var second = await h.PassAsync();

        Assert.Equal(1, first.Delivered);
        Assert.Equal(1, first.Duplicates);
        Assert.Equal(0, second.Delivered);
        Assert.Equal(2, second.Duplicates);
        Assert.Single(h.Gc.External);
        Assert.Single(h.Reader.Started);
        Assert.Single(h.Messages.Snapshot());
    }

    [Fact]
    public async Task Duplicate_back_end_id_without_a_message_id_is_also_one_message()
    {
        var h = new Harness();
        h.Add("b1", Mime.Plain($"Pat <{_senderAddress}>", "no id", "x", messageId: null, auth: "spf=pass"));

        await h.PassAsync();
        var second = await h.PassAsync();

        Assert.Equal(1, second.Duplicates);
        Assert.Single(h.Gc.External);
    }

    [Fact]
    public async Task A_duplicate_of_quarantined_mail_is_not_admitted_twice()
    {
        var h = new Harness();
        h.Add("b1", Failing());

        await h.PassAsync();
        var second = await h.PassAsync();

        Assert.Equal(1, second.Duplicates);
        Assert.Single(await h.Quarantine.ListAsync(null, TestContext.Current.CancellationToken));
        Assert.Single(h.Gc.Notices);
    }

    [Fact]
    public async Task A_crash_after_the_announcement_resumes_without_announcing_twice()
    {
        var h = new Harness();
        h.Add("b1", Clean());
        h.Reader.FailWith = new TimeoutException("model down for pat.private@sender.example.test");

        var failed = await h.PassAsync();

        Assert.Equal(0, failed.Delivered);
        var failure = Assert.Single(failed.Failures);
        Assert.Equal("TimeoutException", failure.Code);
        Assert.DoesNotContain("pat.private", failure.ToString(), StringComparison.Ordinal);
        Assert.False(Assert.Single(h.Messages.Snapshot()).Delivered);

        h.Reader.FailWith = null;
        var resumed = await h.PassAsync();

        Assert.Equal(1, resumed.Delivered);
        Assert.Single(h.Gc.External);
        Assert.Single(h.Messages.Snapshot());
        Assert.True(Assert.Single(h.Messages.Snapshot()).Delivered);
        Assert.Equal(2, h.Reader.Ended.Count);
    }

    [Fact]
    public async Task A_stored_message_is_finished_from_the_store_when_the_back_end_never_offers_it_again()
    {
        // IMAP marks mail seen when it is pulled: after a crash the back end returns nothing, and the stored row is all that is left.
        var h = new Harness();
        h.Add("b1", Clean(body: _secretBody));
        h.Reader.FailWith = new TimeoutException();
        await h.PassAsync();
        h.Backend.Queue.Clear();
        h.Reader.FailWith = null;

        var resumed = await h.PassAsync();

        Assert.Equal((0, 1, 0), (resumed.Pulled, resumed.Delivered, resumed.Deferred));
        Assert.Single(h.Gc.External);
        Assert.Equal(_secretBody, h.Reader.Inputs[^1].TextBody!.Trim());
    }

    [Fact]
    public async Task Messages_are_handled_oldest_first_whatever_order_the_back_end_returns()
    {
        var h = new Harness();
        h.Add("c", Mime.Plain("a@third.example.test", "s", "x", "3@x", "spf=pass"), minutes: 5);
        h.Add("b", Mime.Plain("a@second.example.test", "s", "x", "2@x", "spf=pass"), minutes: 1);
        h.Add("a", Mime.Plain("a@first.example.test", "s", "x", "1@x", "spf=pass"), minutes: 1);

        await h.PassAsync();

        // The two with equal times are ordered by back end id.
        Assert.Equal(["external:first.example.test", "external:second.example.test", "external:third.example.test"], h.Gc.Order);
    }

    [Fact]
    public async Task A_pass_is_bounded_and_the_rest_is_deferred_not_dropped()
    {
        var h = new Harness(maxBatch: 2);
        for (var i = 0; i < 5; i++)
        {
            h.Add($"b{i}", Clean($"m{i}@sender.example.test"), i);
        }

        var first = await h.PassAsync();
        var second = await h.PassAsync();
        var third = await h.PassAsync();

        // Every pulled message is stored on the first pass; only the batch of heavy work is bounded.
        Assert.Equal((5, 2, 3), (first.Pulled, first.Delivered, first.Deferred));
        Assert.Equal((2, 1, 5), (second.Delivered, second.Deferred, second.Duplicates));
        Assert.Equal((1, 0), (third.Delivered, third.Deferred));
        Assert.Equal(5, h.Gc.External.Count);
        Assert.Equal(5, h.Messages.Snapshot().Count);
    }

    [Fact]
    public async Task A_back_end_outage_is_a_result_not_an_exception_and_nothing_is_lost()
    {
        var h = new Harness();
        h.Add("b1", Clean());
        h.Backend.FailWith = new HttpRequestException("503 from pat.private@sender.example.test");

        var down = await h.PassAsync();

        Assert.Equal(0, down.Pulled);
        var failure = Assert.Single(down.Failures);
        Assert.Equal("backend_unavailable:HttpRequestException", failure.Code);
        Assert.DoesNotContain("pat.private", failure.ToString(), StringComparison.Ordinal);

        h.Backend.FailWith = null;
        var up = await h.PassAsync();

        Assert.Equal(1, up.Delivered);
    }

    [Fact]
    public async Task The_first_failure_ends_the_mailbox_pass_so_a_later_message_never_overtakes_an_earlier_one()
    {
        var h = new Harness();
        h.Add("b1", Clean("1@x.example.test"), 0);
        h.Add("b2", Clean("2@x.example.test"), 1);
        h.Reader.FailWith = new InvalidOperationException("boom");

        var result = await h.PassAsync();

        Assert.Single(result.Failures);
        // Both are stored; neither was finished, and the second never started.
        Assert.Equal(2, result.Deferred);
        Assert.Single(h.Reader.Started);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var h = new Harness();
        h.Add("b1", Clean());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Pipeline.ProcessMailboxAsync(h.Mailbox, cts.Token));
    }

    [Fact]
    public async Task Garbage_with_no_sender_is_held_not_a_crash()
    {
        var h = new Harness();
        h.Backend.Queue.Add(new RawMail("g1", h.Mailbox.Box, new byte[] { 0, 255, 1, 254, 13, 10, 0, 0, 200 }, Harness.T0));

        var result = await h.PassAsync();

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Quarantined);
        var item = Assert.Single(await h.Quarantine.ListAsync(QuarantineState.Held, TestContext.Current.CancellationToken));
        Assert.Equal(InboundMailOptions.UnknownSenderDomain, item.SenderDomain);
        Assert.Single(item.ReasonCodes);
        Assert.True(item.ReasonCodes[0] is "MissingSender" or "MalformedMessage");
    }

    [Fact]
    public async Task A_message_that_breaks_a_parse_limit_is_held()
    {
        var h = new Harness(new MailLimits { MaxParts = 3 });
        var parts = new StringBuilder("From: a@sender.example.test\nMIME-Version: 1.0\nContent-Type: multipart/mixed; boundary=\"x\"\n\n");
        for (var i = 0; i < 6; i++)
        {
            parts.Append("--x\nContent-Type: text/plain\n\nhi\n");
        }

        parts.Append("--x--\n");
        h.Add("p1", parts.ToString());

        var result = await h.PassAsync();

        Assert.Equal(1, result.Quarantined);
        Assert.Contains("Limit_parts", Assert.Single(await h.Quarantine.ListAsync(null, TestContext.Current.CancellationToken)).ReasonCodes);
        Assert.Empty(h.Reader.Started);
    }

    [Fact]
    public async Task A_message_over_the_byte_limit_is_held()
    {
        var h = new Harness(new MailLimits { MaxMessageBytes = 400 });
        h.Add("big", Clean(body: new string('x', 2000)));

        var result = await h.PassAsync();

        Assert.Equal(1, result.Quarantined);
        Assert.Contains("Limit_bytes", Assert.Single(await h.Quarantine.ListAsync(null, TestContext.Current.CancellationToken)).ReasonCodes);
    }

    [Fact]
    public async Task A_zip_bomb_lands_in_quarantine()
    {
        var limits = new MailLimits { MaxArchiveUncompressedBytes = 1024 * 1024 };
        var h = new Harness(limits);
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("zeros.txt", CompressionLevel.SmallestSize).Open();
            entry.Write(new byte[4 * 1024 * 1024]);
        }

        h.Add("z1", Mime.WithAttachment($"Pat <{_senderAddress}>", "z@x.example.test", "notes.txt", zip.ToArray()));

        var result = await h.PassAsync();

        Assert.True(result.IsClean);
        Assert.Equal(1, result.Quarantined);
        Assert.Contains("NestedArchive", Assert.Single(await h.Quarantine.ListAsync(null, TestContext.Current.CancellationToken)).ReasonCodes);
        Assert.Empty(h.Reader.Started);
        Assert.Empty(h.Gc.External);
    }

    [Fact]
    public async Task A_display_name_that_shows_another_address_is_held()
    {
        var h = new Harness();
        h.Add("d1", Mime.Plain("\"ceo@bank.example.test\" <attacker@sender.example.test>", "s", "x", "d@x", "spf=pass"));

        await h.PassAsync();

        Assert.Contains("DisplayNameMismatch", Assert.Single(await h.Quarantine.ListAsync(null, TestContext.Current.CancellationToken)).ReasonCodes);
    }

    [Fact]
    public async Task Events_and_results_carry_ids_and_codes_only()
    {
        var h = new Harness();
        h.Add("b1", Clean(subject: _secretSubject, body: _secretBody));
        h.Add("b2", Failing("bad2@sender.example.test"), 1);

        var result = await h.PassAsync();

        var everything = string.Join('\n', h.Events.Events.Select(e => e.ToString())) + result;
        Assert.Contains(h.Events.Events, e => e.Kind == "delivered");
        Assert.Contains(h.Events.Events, e => e.Kind == "quarantined");
        Assert.DoesNotContain(_senderAddress, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(_secretBody, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(_secretSubject, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_worker_passes_over_every_mailbox_and_one_outage_does_not_stop_the_others()
    {
        var down = new Harness();
        down.Backend.FailWith = new IOException("down");
        var up = new Harness();
        up.Add("b1", Clean());
        var worker = new InboundMailWorker(new ListDirectory(down.Mailbox, up.Mailbox), new RoutingPipeline(down, up));

        var pass = await worker.RunPassAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, pass.Mailboxes.Count);
        Assert.Equal(1, pass.Failures);
        Assert.Equal(1, pass.Mailboxes[1].Delivered);
    }

    [Fact]
    public async Task A_directory_failure_is_reported_in_the_result()
    {
        var worker = new InboundMailWorker(new ThrowingDirectory(), new RoutingPipeline());

        var pass = await worker.RunPassAsync(TestContext.Current.CancellationToken);

        Assert.Equal("directory_unavailable:InvalidOperationException", Assert.Single(Assert.Single(pass.Mailboxes).Failures).Code);
    }

    private sealed class ListDirectory(params InboundMailboxBinding[] mailboxes) : IInboundMailboxDirectory
    {
        public Task<IReadOnlyList<InboundMailboxBinding>> ListActiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InboundMailboxBinding>>(mailboxes);
    }

    private sealed class ThrowingDirectory : IInboundMailboxDirectory
    {
        public Task<IReadOnlyList<InboundMailboxBinding>> ListActiveAsync(CancellationToken ct) => throw new InvalidOperationException("db down");
    }

    private sealed class RoutingPipeline(params Harness[] harnesses) : IInboundMailPipeline
    {
        public Task<MailboxPassResult> ProcessMailboxAsync(InboundMailboxBinding mailbox, CancellationToken ct) =>
            harnesses.Single(h => h.Mailbox.MailboxId == mailbox.MailboxId).Pipeline.ProcessMailboxAsync(mailbox, ct);
    }
}
