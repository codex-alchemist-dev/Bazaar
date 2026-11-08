namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// One catalogue row from FreeLLMAPI's <c>GET /v1/models</c>. <c>ExecutionStatus</c> is FreeLLMAPI's live answer to
/// "would a request work right now": <c>ready</c>, <c>needsKey</c> or <c>exhausted</c>. Null when the row carries none.
/// </summary>
public sealed record FreeLlmApiModel(string Id, string? ExecutionStatus)
{
    public const string ReadyStatus = "ready";
}
