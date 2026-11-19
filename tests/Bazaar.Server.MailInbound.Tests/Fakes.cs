using System.Text;
using Bazaar.Server.Mail;
using Bazaar.Server.Mail.Parsing;
using Bazaar.Server.Mail.Quarantine;

namespace Bazaar.Server.MailInbound.Tests;

internal sealed class FakeBackend(string name = "backend") : IMailBackend
{
    public List<RawMail> Queue { get; } = [];

    public Exception? FailWith { get; set; }

    public int Receives { get; private set; }

    public string Name { get; } = name;

    public Task<IReadOnlyList<RawMail>> ReceiveAsync(MailboxRef box, CancellationToken ct)
    {
        Receives++;
        if (FailWith is not null)
        {
            throw FailWith;
        }

        // Redelivers everything on every call: the back end only forgets what the pipeline has finished, and a duplicate is normal.
        return Task.FromResult<IReadOnlyList<RawMail>>([.. Queue]);
    }

    public Task<SendReceipt> SendAsync(OutboundMail mail, CancellationToken ct) => throw new NotSupportedException();

    public Task<BackendHealth> ProbeAsync(CancellationToken ct) => Task.FromResult(new BackendHealth(true, null));
}

/// <summary>A GC double: records every post and answers idempotently on the mail message id, like the real writer.</summary>
internal sealed class FakeGc : IInboxGcPoster
{
    private readonly Dictionary<Guid, Guid> _external = [];
    private readonly HashSet<Guid> _notices = [];
    private readonly HashSet<Guid> _handovers = [];

    public List<ExternalMailEntry> External { get; } = [];

    public List<QuarantineNotice> Notices { get; } = [];

    public List<MailTriageHandover> Handovers { get; } = [];

    public List<string> Order { get; } = [];

    /// <summary>Every field of everything posted, as text: what an agent reading the GC could see.</summary>
    public string Everything() => string.Join('\n', External.Select(e => e.ToString()).Concat(Notices.Select(n => n.ToString())).Concat(Handovers.Select(h => h.ToString())));

    public Task<Guid> PostExternalMailAsync(ExternalMailEntry entry, CancellationToken ct)
    {
        if (_external.TryGetValue(entry.MailMessageId, out var existing))
        {
            return Task.FromResult(existing);
        }

        var id = Guid.NewGuid();
        _external[entry.MailMessageId] = id;
        External.Add(entry);
        Order.Add("external:" + entry.SenderDomain);
        return Task.FromResult(id);
    }

    public Task PostQuarantineNoticeAsync(QuarantineNotice notice, CancellationToken ct)
    {
        if (_notices.Add(notice.MailMessageId))
        {
            Notices.Add(notice);
        }

        return Task.CompletedTask;
    }

    public Task PostTriageHandoverAsync(Guid inboxGcId, MailTriageHandover handover, CancellationToken ct)
    {
        if (_handovers.Add(handover.MailMessageId))
        {
            Handovers.Add(handover);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Reader session, taint and model doubles in one: it checks the taint is hard before the model sees anything.</summary>
internal sealed class FakeReader : IMailReaderSessions, IReaderSessionTaint, IMailTriageReader
{
    private readonly HashSet<Guid> _tainted = [];

    public List<Guid> Started { get; } = [];

    public List<Guid> Ended { get; } = [];

    public List<Guid> TaintedSessions { get; } = [];

    public List<MailReaderInput> Inputs { get; } = [];

    public bool TaintWasHardBeforeRead { get; private set; } = true;

    public Func<MailReaderInput, MailTriageHandover>? Decide { get; set; }

    public Exception? FailWith { get; set; }

    public Task<ReaderSessionHandle> StartAsync(ReaderSessionRequest request, CancellationToken ct)
    {
        Started.Add(request.MailMessageId);
        return Task.FromResult(new ReaderSessionHandle(Guid.NewGuid()));
    }

    public Task EndAsync(ReaderSessionHandle session, CancellationToken ct)
    {
        Ended.Add(session.SessionId);
        return Task.CompletedTask;
    }

    public Task MarkHardAsync(Guid sessionId, Guid mailMessageId, CancellationToken ct)
    {
        _tainted.Add(sessionId);
        TaintedSessions.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task<MailTriageHandover> TriageAsync(ReaderSessionHandle session, MailReaderInput input, CancellationToken ct)
    {
        TaintWasHardBeforeRead &= _tainted.Contains(session.SessionId);
        Inputs.Add(input);
        if (FailWith is not null)
        {
            throw FailWith;
        }

        return Task.FromResult(Decide?.Invoke(input)
            ?? new MailTriageHandover(input.MailMessageId, MailTriageVerdict.Routine, [MailTriageReasonCodes.Notification]));
    }
}

internal sealed class RecordingEvents : IInboundMailEvents
{
    public List<InboundEvent> Events { get; } = [];

    public void Report(InboundEvent inboundEvent) => Events.Add(inboundEvent);
}

/// <summary>Not encryption: a marker transform, so a test can tell sealed bytes from the clear message.</summary>
internal sealed class FakeSealer : IMailBodySealer
{
    public SealedBody Seal(Guid personId, ReadOnlyMemory<byte> raw) =>
        new(Guid.NewGuid(), [.. raw.ToArray().Select(b => (byte)(b ^ 0x5A))], [1, 2, 3]);

    public byte[] Open(Guid personId, SealedBody sealedBody) => [.. sealedBody.Ciphertext.Select(b => (byte)(b ^ 0x5A))];
}

internal static class Mime
{
    public static RawMail Raw(string backendId, string text, DateTimeOffset at, MailboxRef? box = null) =>
        new(backendId, box ?? new MailboxRef("agents.example.test", "agent"), Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n")), at);

    public static string Plain(string from, string subject, string body, string? messageId = null, string? auth = null) =>
        $"From: {from}\nTo: agent@agents.example.test\nSubject: {subject}\n"
        + (messageId is null ? string.Empty : $"Message-ID: <{messageId}>\n")
        + (auth is null ? string.Empty : $"Authentication-Results: agents.example.test; {auth}\n")
        + $"Content-Type: text/plain\n\n{body}";

    public static string WithAttachment(string from, string messageId, string fileName, byte[] content) =>
        $"From: {from}\nTo: agent@agents.example.test\nSubject: files\nMessage-ID: <{messageId}>\nMIME-Version: 1.0\n"
        + "Content-Type: multipart/mixed; boundary=\"bb\"\n\n--bb\nContent-Type: text/plain\n\nsee attached\n"
        + $"--bb\nContent-Type: application/octet-stream; name=\"{fileName}\"\nContent-Disposition: attachment; filename=\"{fileName}\"\n"
        + $"Content-Transfer-Encoding: base64\n\n{Convert.ToBase64String(content, Base64FormattingOptions.InsertLineBreaks)}\n--bb--\n";
}

/// <summary>Everything a pipeline test needs, wired to the fakes.</summary>
internal sealed class Harness
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    public FakeBackend Backend { get; } = new();

    public FakeGc Gc { get; } = new();

    public FakeReader Reader { get; } = new();

    public RecordingEvents Events { get; } = new();

    public InMemoryMailQuarantineStore Quarantine { get; } = new();

    public InMemoryMailMessageStore Messages { get; }

    public InboundMailboxBinding Mailbox { get; }

    public InboundMailPipeline Pipeline { get; }

    public Harness(MailLimits? limits = null, int maxBatch = 50)
    {
        Messages = new InMemoryMailMessageStore(Quarantine);
        Mailbox = new InboundMailboxBinding(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new MailboxRef("agents.example.test", "agent"), Backend);
        var screeningOptions = new MailScreeningOptions { Limits = limits ?? MailLimits.Defaults };
        Pipeline = new InboundMailPipeline(
            Messages, Quarantine, new FakeSealer(),
            new MailScreeningService(new InMemoryProtectedMailDomains(["agents.example.test"]), screeningOptions), screeningOptions,
            Gc, Reader, Reader, Reader, new InboundMailOptions { MaxBatchSize = maxBatch }, Events);
    }

    public Task<MailboxPassResult> PassAsync() => Pipeline.ProcessMailboxAsync(Mailbox, TestContext.Current.CancellationToken);

    public void Add(string backendId, string text, int minutes = 0) => Backend.Queue.Add(Mime.Raw(backendId, text, T0.AddMinutes(minutes)));
}
