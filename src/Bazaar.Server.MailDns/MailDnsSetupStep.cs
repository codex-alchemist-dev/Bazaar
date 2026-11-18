using System.Globalization;
using System.Text;
using Bazaar.Server.Setup;

namespace Bazaar.Server.MailDns;

/// <summary>
/// Setup step 11, Email (doc 45 section 1.3 and doc 22). Does nothing when <c>email.enabled</c> is false. When true it runs the
/// DNS checker for the plan's mail domain and reports the result as a message, counts first. The report is advice: DNS may not
/// have propagated, so this step always succeeds and never blocks the apply. It only reads DNS, so running it again is safe, and
/// there is nothing to roll back. It holds no domain, provider or selector of its own and handles no secret.
/// </summary>
public sealed class MailDnsSetupStep(IMailDnsChecker checker) : ISetupStep
{
    /// <summary>The step id written to the audit row.</summary>
    public const string StepId = "email.dns";

    /// <summary>
    /// The position in the apply order: the doc 45 step number. Step 9 (model sources) runs before it and step 12 (scam-mail
    /// protocol) after it. Other steps are expected to use their own doc step numbers.
    /// </summary>
    public const int StepOrder = 11;

    public string Id => StepId;

    public int Order => StepOrder;

    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Email.Enabled)
        {
            return SetupStepResult.Ok;
        }

        var email = plan.Email;
        if (string.IsNullOrWhiteSpace(email.Domain))
        {
            return new SetupStepResult(true, "Email is on but the plan names no mail domain, so the DNS check was skipped. Set email.domain to run it.");
        }

        var request = new MailDnsRequest(
            email.Domain,
            MailDnsBackendKinds.IsKnown(email.BackendKind) ? email.BackendKind!.ToLowerInvariant() : null,
            string.IsNullOrWhiteSpace(email.DkimSelector) ? null : email.DkimSelector,
            DmarcReportAddress: string.IsNullOrWhiteSpace(email.DmarcReportAddress) ? null : email.DmarcReportAddress,
            SelfHosted: email.SelfHosted);

        MailDnsReport report;
        try
        {
            report = await checker.CheckAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The checker is built not to throw. If a replacement does, the apply still goes on, and the text carries no exception detail.
            return new SetupStepResult(true, "The mail DNS check could not run. Run it again later from the email settings.");
        }

        return new SetupStepResult(true, Describe(report));
    }

    /// <summary>The report as text for the operator: counts on the first line, then one line per check that needs attention.</summary>
    internal static string Describe(MailDnsReport report)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Mail DNS check for {report.Domain}: ")
            .Append(CultureInfo.InvariantCulture, $"{report.Count(DnsCheckStatus.Pass)} pass, {report.Count(DnsCheckStatus.Warn)} warn, ")
            .Append(CultureInfo.InvariantCulture, $"{report.Count(DnsCheckStatus.Fail)} fail, {report.Count(DnsCheckStatus.NotApplicable)} not applicable. ")
            .Append("This is advice and does not block setup.");

        foreach (var result in report.Results.Where(r => r.Status is DnsCheckStatus.Warn or DnsCheckStatus.Fail))
        {
            text.Append('\n').Append(result.Kind).Append(' ').Append(result.Status).Append(": ").Append(result.Detail);
            if (result.Fix.Length > 0)
            {
                text.Append(" Fix: ").Append(result.Fix);
            }
        }

        return text.ToString();
    }
}
