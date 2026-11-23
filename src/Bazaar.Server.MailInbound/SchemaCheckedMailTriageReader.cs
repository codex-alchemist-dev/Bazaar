namespace Bazaar.Server.MailInbound;

/// <summary>
/// The triage reader path: asks the model port for a raw payload and checks it against the <c>mail_triage</c> schema. Output that
/// does not validate fails closed to an <see cref="MailTriageVerdict.Escalate"/> hand-over, which the pipeline admits to quarantine
/// as a reader. The fallback uses a reader code, so the pipeline's own check accepts it. A thrown model error is not swallowed here.
/// </summary>
public sealed class SchemaCheckedMailTriageReader(IMailTriageModel model) : IMailTriageReader
{
    public async Task<MailTriageHandover> TriageAsync(ReaderSessionHandle session, MailReaderInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        var payload = await model.CompleteAsync(session, input, ct).ConfigureAwait(false);
        var result = MailTriageHandoverValidator.Validate(payload, input.MailMessageId);
        return result.Handover ?? new MailTriageHandover(input.MailMessageId, MailTriageVerdict.Escalate, [MailTriageReasonCodes.Unclear]);
    }
}
