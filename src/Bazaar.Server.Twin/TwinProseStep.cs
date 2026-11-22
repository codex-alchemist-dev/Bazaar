using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Twin;

/// <summary>
/// The model seam for the prose step: one call, no tools, prompt in, raw reply out. The real implementation is a reader session
/// (doc 15; doc 25 "Prose is written in a reader session with no tools") and plugs in later; tests use a fake. Implementations
/// must offer the model no tools and must treat the reply as untrusted text.
/// </summary>
public interface ITwinProseWriter
{
    /// <summary>Returns the model's reply to <paramref name="prompt"/>. May throw; the step reports the failure.</summary>
    Task<string> DraftAsync(string prompt, CancellationToken ct);
}

/// <summary>How a prose step ended.</summary>
public enum TwinProseOutcome
{
    /// <summary>The page was written (or already matched the Vault copy; see <see cref="TwinProseResult.Edit"/>).</summary>
    Written,

    /// <summary>The reply held no sentence with a valid source, so nothing was written.</summary>
    AllDropped,

    /// <summary>The model call failed. Nothing was written.</summary>
    ModelFailed,

    /// <summary>The reply was not the expected JSON array. Nothing was written.</summary>
    ReplyUnreadable,
}

/// <summary>The result of one prose step.</summary>
/// <param name="Page">The validated page, or null when the model call failed or the reply was unreadable.</param>
/// <param name="Edit">The Vault write result when <see cref="Outcome"/> is <see cref="TwinProseOutcome.Written"/>.</param>
/// <param name="Error">A short reason for <see cref="TwinProseOutcome.ModelFailed"/> and <see cref="TwinProseOutcome.ReplyUnreadable"/>.</param>
public sealed record TwinProseResult(TwinProseOutcome Outcome, ValidatedTwinPage? Page, VaultEditResult? Edit, string? Error);

/// <summary>
/// Doc 25 WP4: facts in, one validated prose page out. Builds the prompt from the facts only, calls the reader seam, parses and
/// validates the reply, and writes the page through <see cref="TwinPageWriter"/>. Writes nothing when every sentence is dropped.
/// </summary>
public sealed class TwinProseStep
{
    private readonly ITwinProseWriter _model;
    private readonly TwinPageWriter _pages;

    public TwinProseStep(ITwinProseWriter model, TwinPageWriter pages)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
    }

    /// <exception cref="ArgumentException">A name is invalid, or the request has no facts.</exception>
    public async Task<TwinProseResult> RunAsync(TwinProseRequest request, string baseCommit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        TwinPaths.ValidateSegment(request.Project, nameof(request.Project));
        TwinPaths.ValidateSegment(request.Page, nameof(request.Page));
        TwinPaths.ValidateTitle(request.Title);
        if (request.Facts is null || request.Facts.Count == 0)
        {
            throw new ArgumentException("A page needs at least one fact.", nameof(request));
        }

        string reply;
        try
        {
            reply = await _model.DraftAsync(TwinProsePrompt.Build(request), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TwinProseResult(TwinProseOutcome.ModelFailed, null, null, ex.Message);
        }

        IReadOnlyList<TwinProseSentence> draft;
        try
        {
            draft = TwinReplyParser.Parse(reply ?? "");
        }
        catch (FormatException ex)
        {
            return new TwinProseResult(TwinProseOutcome.ReplyUnreadable, null, null, ex.Message);
        }

        var page = TwinRefValidator.Validate(request, draft);
        if (page.Sentences.Count == 0)
        {
            return new TwinProseResult(TwinProseOutcome.AllDropped, page, null, null);
        }

        var edit = await _pages.WriteAsync(page, baseCommit, ct);
        return new TwinProseResult(TwinProseOutcome.Written, page, edit, null);
    }
}
