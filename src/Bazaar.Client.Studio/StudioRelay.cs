using System.Text;
using System.Text.Json;
using Bazaar.Client.Studio.Abstractions;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Studio;

/// <summary>
/// Relays granted Studio tool calls (docs/design/20-engine-integration-api.md, 28-windows-client.md). Check order: studio_id present,
/// tool in the allow-list, grant verified (fail closed), Studio open and studio_id known, then forward as MCP <c>tools/call</c>.
/// Keeps no grants and no audit. Result content is untrusted data and is returned as is (size-capped), never interpreted.
/// </summary>
public sealed class StudioRelay : IStudioRelay
{
    private const string _listTool = "list_roblox_studios";
    private readonly IStudioMcpTransport _transport;
    private readonly IStudioGrantVerifier _verifier;
    private readonly StudioRelayOptions _options;

    /// <summary>Creates a relay over a transport and a grant verifier.</summary>
    public StudioRelay(IStudioMcpTransport transport, IStudioGrantVerifier verifier, StudioRelayOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _options = options ?? new StudioRelayOptions();
    }

    /// <inheritdoc />
    public async Task<StudioRelayResult> RelayAsync(StudioRelayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grantId = request.GrantId ?? "";

        if (string.IsNullOrWhiteSpace(request.StudioId))
        {
            return Fail(grantId, StudioRelayErrorCode.StudioIdRequired, "studio_id is required; there is no default Studio");
        }

        if (!StudioToolCatalog.TryGetRiskClass(request.Tool, out var riskClass))
        {
            return Fail(grantId, StudioRelayErrorCode.ToolNotAllowed, "tool is not in the Studio allow-list");
        }

        if (string.IsNullOrWhiteSpace(request.GrantId))
        {
            return Fail(grantId, StudioRelayErrorCode.GrantDenied, "no grant reference");
        }

        if (request.Args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return Fail(grantId, StudioRelayErrorCode.ToolNotAllowed, "tool arguments must be a JSON object");
        }

        if (!await VerifyGrantAsync(request, riskClass, cancellationToken).ConfigureAwait(false))
        {
            return Fail(grantId, StudioRelayErrorCode.GrantDenied, "grant does not cover this tool, or could not be verified");
        }

        try
        {
            var unavailable = await CheckStudioAsync(request.StudioId, grantId, cancellationToken).ConfigureAwait(false);
            if (unavailable is not null)
            {
                return unavailable;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.CallTimeout);
            try
            {
                var result = await _transport.CallAsync("tools/call", BuildCallParams(request), timeout.Token).ConfigureAwait(false);
                var isError = result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("isError", out var flag)
                    && flag.ValueKind == JsonValueKind.True;
                return new StudioRelayResult(grantId, !isError, Cap(result));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail(grantId, StudioRelayErrorCode.Timeout, "Studio did not answer within the call timeout");
            }
        }
        catch (StudioTransportException ex) when (ex.RpcCode is not null)
        {
            // Studio refused or failed the call: report it as a failed result. The message is Studio's text, so it stays data.
            return new StudioRelayResult(grantId, false, RpcErrorPayload(ex));
        }
        catch (StudioTransportException)
        {
            return Fail(grantId, StudioRelayErrorCode.Unavailable, "Studio bridge connection was lost");
        }
    }

    private static StudioRelayResult Fail(string grantId, StudioRelayErrorCode code, string reason)
        => new(grantId, false, new StudioRelayError(code, reason).ToPayload());

    private static JsonElement BuildCallParams(StudioRelayRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", request.Tool);
            writer.WriteStartObject("arguments");
            if (request.Args.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in request.Args.EnumerateObject())
                {
                    // The relay's studio_id wins; an agent cannot smuggle a different target in the args.
                    if (property.NameEquals("studio_id"))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
            }

            writer.WriteString("studio_id", request.StudioId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Parse(stream.ToArray());
    }

    private static JsonElement RpcErrorPayload(StudioTransportException ex)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("code", "StudioError");
            writer.WriteNumber("rpcCode", ex.RpcCode ?? 0);
            writer.WriteString("reason", ex.Message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Parse(stream.ToArray());
    }

    private static JsonElement Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private async Task<bool> VerifyGrantAsync(StudioRelayRequest request, RiskClass riskClass, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CheckTimeout);
        try
        {
            return await _verifier.CoversAsync(request.GrantId, request.Tool, riskClass, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Fail closed: any verifier failure, whatever its type, denies the call.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>Returns a failure when Studio is not reachable or the studio_id is not open; null when the call may proceed.</summary>
    private async Task<StudioRelayResult?> CheckStudioAsync(string studioId, string grantId, CancellationToken cancellationToken)
    {
        if (!_transport.IsStudioOpen)
        {
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startTimeout.CancelAfter(_options.CheckTimeout);
            try
            {
                await _transport.StartAsync(startTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Any start failure means the bridge is offline.
            catch (Exception)
#pragma warning restore CA1031
            {
                return Fail(grantId, StudioRelayErrorCode.Unavailable, "Roblox Studio is not open or its MCP server could not be started");
            }

            if (!_transport.IsStudioOpen)
            {
                return Fail(grantId, StudioRelayErrorCode.Unavailable, "Roblox Studio is not open");
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CheckTimeout);
        JsonElement listing;
        try
        {
            using var args = JsonDocument.Parse($"{{\"name\":\"{_listTool}\",\"arguments\":{{}}}}");
            listing = await _transport.CallAsync("tools/call", args.RootElement.Clone(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(grantId, StudioRelayErrorCode.Timeout, "Studio did not list its open places in time");
        }
        catch (StudioTransportException)
        {
            return Fail(grantId, StudioRelayErrorCode.Unavailable, "Studio bridge connection was lost");
        }

        var ids = StudioListing.ParseStudioIds(listing);
        if (ids.Count == 0)
        {
            return Fail(grantId, StudioRelayErrorCode.Unavailable, "no Roblox Studio is open");
        }

        return ids.Contains(studioId)
            ? null
            : Fail(grantId, StudioRelayErrorCode.StudioUnknown, "studio_id is not one of the open Studios");
    }

    /// <summary>Replaces an oversized result with a small marker object holding a prefix of its JSON.</summary>
    private JsonElement Cap(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Undefined)
        {
            return Parse("null"u8.ToArray());
        }

        var raw = result.GetRawText();
        var bytes = Encoding.UTF8.GetByteCount(raw);
        if (bytes <= _options.MaxPayloadBytes)
        {
            return result.Clone();
        }

        var take = Math.Min(raw.Length, Math.Max(0, _options.MaxPayloadBytes / 4));
        if (take > 0 && take < raw.Length && char.IsHighSurrogate(raw[take - 1]))
        {
            take--;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("truncated", true);
            writer.WriteNumber("originalBytes", bytes);
            writer.WriteString("preview", raw[..take]);
            writer.WriteString("marker", "[truncated: result exceeded the bridge size cap]");
            writer.WriteEndObject();
        }

        return Parse(stream.ToArray());
    }
}
