using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.Browser.Cdp;

namespace Bazaar.Server.Browser;

/// <summary>
/// <see cref="IBrowserGuestHost"/> over <see cref="IBrowserGuestPipe"/>: starts Chromium with a debugging pipe and speaks
/// CDP over it. Calls on one handle are serialised. Exception messages never contain cookie names or values.
/// </summary>
public sealed class GuestPipeBrowserHost(IBrowserGuestPipe pipe, BrowserHostOptions? options = null) : IBrowserGuestHost
{
    private readonly TimeSpan _timeout = (options ?? new BrowserHostOptions()).CdpTimeout;
    private readonly ConcurrentDictionary<string, HandleState> _handles = new(StringComparer.Ordinal);

    public async Task<BrowserProcessHandle> StartAsync(BrowserLaunchSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.SessionId))
        {
            throw new ArgumentException("Launch spec has no session id.", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.ExecutablePath))
        {
            throw new ArgumentException("Launch spec has no executable path.", nameof(spec));
        }

        ArgumentNullException.ThrowIfNull(spec.Arguments);

        var processId = await pipe.StartProcessAsync(spec.GuestOs, spec.ExecutablePath, spec.Arguments, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(processId))
        {
            throw new InvalidOperationException("The guest returned no process id.");
        }

        _handles[processId] = new HandleState();
        return new BrowserProcessHandle(spec.SessionId, processId);
    }

    public async Task SetCookiesAsync(BrowserProcessHandle handle, IReadOnlyList<BrowserCookieEntry> cookies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(cookies);
        if (cookies.Count == 0)
        {
            return;
        }

        var state = _handles.GetOrAdd(handle.GuestProcessId, static _ => new HandleState());
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = ++state.LastId;
            var request = CdpCookieMapper.BuildSetCookiesRequest(id, cookies);
            await SendAndAwaitAsync(handle.GuestProcessId, request, id, "Network.setCookies", ct).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task StopAsync(BrowserProcessHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_handles.TryRemove(handle.GuestProcessId, out var state))
        {
            return;
        }

        try
        {
            try
            {
                await state.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var id = ++state.LastId;
                    var request = CdpCookieMapper.BuildClearCookiesRequest(id);
                    await SendAndAwaitAsync(handle.GuestProcessId, request, id, "Network.clearBrowserCookies", ct).ConfigureAwait(false);
                }
                finally
                {
                    state.Gate.Release();
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Best effort: the process is killed next whatever happened.
            }
        }
        finally
        {
            await pipe.KillProcessTreeAsync(handle.GuestProcessId, ct).ConfigureAwait(false);
        }
    }

    private async Task SendAndAwaitAsync(string processId, byte[] json, int id, string method, CancellationToken ct)
    {
        var frame = new byte[json.Length + 1];
        json.CopyTo(frame, 0);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            await pipe.WritePipeAsync(processId, frame, cts.Token).ConfigureAwait(false);
            while (true)
            {
                var reply = await pipe.ReadPipeFrameAsync(processId, cts.Token).ConfigureAwait(false);
                if (!TryReadId(reply, out var replyId) || replyId != id)
                {
                    continue;
                }

                if (!CdpResponse.IsSuccess(reply, id))
                {
                    throw new InvalidOperationException($"The browser refused {method}.");
                }

                return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The browser did not answer {method} within {_timeout.TotalSeconds:0.#} s.");
        }
        catch (Exception e) when (e is not (OperationCanceledException or InvalidOperationException))
        {
            throw new InvalidOperationException($"The guest pipe failed during {method} ({e.GetType().Name}).");
        }
    }

    private static bool TryReadId(ReadOnlySpan<byte> json, out int id)
    {
        id = 0;
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isId = reader.ValueTextEquals("id"u8);
                if (!reader.Read())
                {
                    return false;
                }

                if (isId)
                {
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out id);
                }

                reader.Skip();
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class HandleState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int LastId;
    }
}
