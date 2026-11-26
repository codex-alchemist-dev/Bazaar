using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.GeneticMemory;

/// <summary>
/// [PROPOSED] <see cref="IGmtQueueClient"/> over the server's review routes (<c>GET /gmt/queue</c>, <c>POST /gmt/queue/{id}/accept</c>
/// and <c>/reject</c>). The <see cref="HttpClient"/> carries the base address and the bearer token. The role is not sent: the server
/// decides what the caller may review from who they are, so a client cannot claim more. Server error codes become short messages; no
/// server text is shown as is.
/// </summary>
public sealed class HttpGmtQueueClient(HttpClient http) : IGmtQueueClient
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GmtQueueItem>> ListAsync(GmtReviewerRole role, CancellationToken ct)
    {
        using var response = await http.GetAsync("gmt/queue", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<List<Dto>>(_json, ct).ConfigureAwait(false) ?? [];
        return [.. items.Select(d => new GmtQueueItem(
            d.Id, d.ChildAgent, d.TargetAgent, d.ContentClass, d.Kind, d.Title, d.Body, d.TraceRef, d.Tainted, d.OwnerOnly, d.StagedAt))];
    }

    /// <inheritdoc />
    public async Task<GmtDecisionResult> AcceptAsync(Guid id, GmtReviewerRole role, string? editedTitle, string? editedBody, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"gmt/queue/{id:D}/accept", new AcceptBody(editedTitle, editedBody), _json, ct).ConfigureAwait(false);
        return await ResultAsync(response, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GmtDecisionResult> RejectAsync(Guid id, GmtReviewerRole role, CancellationToken ct)
    {
        using var response = await http.PostAsync($"gmt/queue/{id:D}/reject", null, ct).ConfigureAwait(false);
        return await ResultAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<GmtDecisionResult> ResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return new GmtDecisionResult(true);
        }

        string? code = null;
        try
        {
            code = (await response.Content.ReadFromJsonAsync<ErrorBody>(_json, ct).ConfigureAwait(false))?.Error;
        }
        catch (JsonException)
        {
        }

        return new GmtDecisionResult(false, code switch
        {
            "secret_found" => "The text holds a key or token. Remove it and try again.",
            "personal_data" => "The text names a person, customer or project. Remove it and try again.",
            "already_decided" => "Someone already decided this one.",
            "not_found" => "This candidate is gone.",
            "forbidden" => "You may not review this one.",
            "invalid" => "The server could not use this text.",
            _ => "The server could not do this. Nothing changed.",
        });
    }

    private sealed record Dto(
        Guid Id, string ChildAgent, string TargetAgent, string ContentClass, string Kind, string Title, string Body,
        string? TraceRef, bool Tainted, bool OwnerOnly, DateTimeOffset StagedAt);

    private sealed record AcceptBody([property: JsonPropertyName("title")] string? Title, [property: JsonPropertyName("body")] string? Body);

    private sealed record ErrorBody(string? Error);
}
