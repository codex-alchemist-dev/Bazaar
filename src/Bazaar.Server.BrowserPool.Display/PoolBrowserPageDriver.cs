using System.Text.Json;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.BrowserPool.Display;

/// <summary>
/// The <see cref="IBrowserPageDriver"/> for pool browser sessions: a screenshot and page input over the session's DevTools pipe
/// (docs/design/59, section 7). It sends only the methods in <see cref="CdpMethod"/> and has no way to run script, navigate or
/// touch the network, cookies or downloads.
/// <para>
/// Page choice: with <c>--remote-debugging-pipe</c> the connection is to the browser, so the driver lists targets, takes the ones of
/// type <c>page</c>, picks the one with the lowest <c>targetId</c> in ordinal order (so the choice does not depend on the order the
/// browser reports them), and attaches with <c>flatten: true</c>. It keeps that attachment until the browser says the session is
/// gone (error code -32001); then it looks again and retries the command once.
/// </para>
/// <para>
/// One <see cref="DevToolsSession"/> exists per pool session id. It is created on first use and retired once
/// <see cref="IPoolDevToolsPipes.TryGetPipe"/> stops returning the pipe. Calls to one session run one at a time, so input batches
/// never interleave. Coordinates are CSS pixels of the viewport.
/// </para>
/// </summary>
public sealed class PoolBrowserPageDriver : IBrowserPageDriver, IAsyncDisposable
/// never interleave. Coordinates are CSS pixels of the viewport. The pool starts Chromium with a device scale factor of 1 and the
/// screenshot covers the viewport only, so screenshot pixels and input coordinates are one space (<see cref="IPixelExactBrowserPageDriver"/>).
/// </para>
/// </summary>
public sealed class PoolBrowserPageDriver : IPixelExactBrowserPageDriver, IAsyncDisposable
{
    private const int _sessionNotFoundCode = -32001;
    private const int _maxIdLength = 256;

    private static readonly byte[] _screenshotParams = "{\"format\":\"png\",\"captureBeyondViewport\":false}"u8.ToArray();
    private static readonly byte[] _getTargetsParams = "{}"u8.ToArray();
    private static readonly JsonDocumentOptions _documentOptions = new() { MaxDepth = 128 };

    private readonly IPoolDevToolsPipes _pipes;
    private readonly BrowserPageDriverOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PageSession> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the driver over a pool host's pipes.</summary>
    public PoolBrowserPageDriver(IPoolDevToolsPipes pipes, BrowserPageDriverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pipes);
        _pipes = pipes;
        _options = options ?? new BrowserPageDriverOptions();
        _options.Validate();
    }

    /// <summary>
    /// True when the target names a browser session and the pool has a DevTools pipe for it. <see cref="DisplayTarget"/> does not
    /// say whether a browser session is a pool session, so the pipe is what decides.
    /// </summary>
    public bool Supports(DisplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrEmpty(target.BrowserSessionId) || target.BrowserSessionId.Length > _maxIdLength)
        {
            return false;
        }

        Lookup(target.BrowserSessionId, create: false);
        return _pipes.TryGetPipe(target.BrowserSessionId, out _);
    }

    /// <inheritdoc />
    public async Task<byte[]> CaptureScreenshotPngAsync(string sessionId, CancellationToken ct)
    {
        var page = Lookup(CheckId(sessionId), create: true) ?? throw NoPipe();
        await page.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reply = await SendToPageAsync(page, CdpMethod.PageCaptureScreenshot, _screenshotParams, ct).ConfigureAwait(false);
            if (!CdpJson.TryFindScreenshotData(reply, out var start, out var length))
            {
                throw new PageDriverException(PageDriverFailure.BadResponse, "The screenshot reply has no image data.");
            }

            // The size is judged on the base64 text, before any decoding allocates the PNG.
            if (length > BrowserPageDriverOptions.Base64Length(_options.MaxScreenshotBytes))
            {
                throw new PageDriverException(PageDriverFailure.TooLarge, "The screenshot is larger than the limit.");
            }

            if (!CdpJson.TryDecodeBase64(reply.AsSpan(start, length), out var png) || png.Length > _options.MaxScreenshotBytes || !png.AsSpan().StartsWith(CdpJson.PngSignature))
            {
                throw new PageDriverException(PageDriverFailure.BadResponse, "The screenshot reply is not a PNG.");
            }

            return png;
        }
        finally
        {
            page.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DispatchInputAsync(string sessionId, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var page = Lookup(CheckId(sessionId), create: true) ?? throw NoPipe();
        await page.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The whole batch is checked and rendered first, so an invalid action sends nothing.
            var commands = InputTranslator.Translate(actions, page.Pointer, _options);
            foreach (var command in commands)
            {
                await SendToPageAsync(page, command.Method, command.ParamsJson, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            page.Gate.Release();
        }
    }

    /// <summary>Closes every session. The pipes' streams belong to the pool host and are not touched.</summary>
    public async ValueTask DisposeAsync()
    {
        List<PageSession> all;
        lock (_lock)
        {
            _disposed = true;
            all = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var s in all)
        {
            await s.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal int OpenSessionCount
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count;
            }
        }
    }

    private static string CheckId(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > _maxIdLength)
        {
            throw new ArgumentException("The browser session id is empty or too long.", nameof(sessionId));
        }

        return sessionId;
    }

    private static PageDriverException NoPipe() => new(PageDriverFailure.NoPipe, "The pool has no DevTools pipe for this browser session.");

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 and <= _maxIdLength } text)
        {
            return text;
        }

        throw new PageDriverException(PageDriverFailure.BadResponse, "A DevTools reply is missing " + name + ".");
    }

    // Retires sessions whose pipe the pool no longer hands out, so cleanup needs no timer: the display module calls Supports on
    // every request. Returns the session for the id (made on first use) when asked to create, otherwise null.
    private PageSession? Lookup(string sessionId, bool create)
    {
        var retire = new List<PageSession>();
        PageSession? result = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var (id, existing) in _sessions.ToArray())
            {
                if (!_pipes.TryGetPipe(id, out var current) || !ReferenceEquals(current.ToBrowser, existing.Session.Pipe.ToBrowser) || !ReferenceEquals(current.FromBrowser, existing.Session.Pipe.FromBrowser))
                {
                    _sessions.Remove(id);
                    retire.Add(existing);
                }
            }

            if (create)
            {
                if (_sessions.TryGetValue(sessionId, out var found))
                {
                    result = found;
                }
                else if (_pipes.TryGetPipe(sessionId, out var pipe))
                {
                    result = new PageSession(new DevToolsSession(pipe, _options));
                    _sessions.Add(sessionId, result);
                }
            }
        }

        foreach (var old in retire)
        {
            // Not awaited: the call that noticed must not wait on another session's reader loop.
            _ = old.Session.DisposeAsync().AsTask();
        }

        return result;
    }

    // Sends a page command, attaching first when needed. If the browser says the attached session is gone, attaches again and
    // retries the command once; the failed command did not run, so a retry cannot repeat an input.
    private static async Task<byte[]> SendToPageAsync(PageSession page, CdpMethod method, byte[] paramsJson, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var cdpSession = page.CdpSessionId ?? await AttachAsync(page, ct).ConfigureAwait(false);
            try
            {
                return await page.Session.SendAsync(method, cdpSession, paramsJson, ct).ConfigureAwait(false);
            }
            catch (PageDriverException e) when (attempt == 0 && e.Failure == PageDriverFailure.ProtocolError && e.ProtocolCode == _sessionNotFoundCode)
            {
                page.CdpSessionId = null;
            }
        }
    }

    private static async Task<string> AttachAsync(PageSession page, CancellationToken ct)
    {
        var listed = await page.Session.SendAsync(CdpMethod.TargetGetTargets, null, _getTargetsParams, ct).ConfigureAwait(false);
        string? targetId = null;
        using (var doc = ParseReply(listed))
        {
            if (doc.RootElement.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("targetInfos", out var infos)
                && infos.ValueKind == JsonValueKind.Array)
            {
                foreach (var info in infos.EnumerateArray())
                {
                    if (info.ValueKind != JsonValueKind.Object
                        || !info.TryGetProperty("type", out var type)
                        || type.ValueKind != JsonValueKind.String
                        || !type.ValueEquals("page"))
                    {
                        continue;
                    }

                    var id = ReadString(info, "targetId");
                    if (targetId is null || string.CompareOrdinal(id, targetId) < 0)
                    {
                        targetId = id;
                    }
                }
            }
        }

        if (targetId is null)
        {
            throw new PageDriverException(PageDriverFailure.NoPage, "The browser has no page to drive.");
        }

        var attachParams = RenderAttachParams(targetId);
        var attached = await page.Session.SendAsync(CdpMethod.TargetAttachToTarget, null, attachParams, ct).ConfigureAwait(false);
        using var attachedDoc = ParseReply(attached);
        if (!attachedDoc.RootElement.TryGetProperty("result", out var attachResult))
        {
            throw new PageDriverException(PageDriverFailure.BadResponse, "The attach reply has no result.");
        }

        var cdpSessionId = ReadString(attachResult, "sessionId");
        page.CdpSessionId = cdpSessionId;
        return cdpSessionId;
    }

    private static JsonDocument ParseReply(byte[] reply)
    {
        try
        {
            return JsonDocument.Parse(reply, _documentOptions);
        }
        catch (JsonException e)
        {
            throw new PageDriverException(PageDriverFailure.BadResponse, "A DevTools reply is not valid JSON.", e);
        }
    }

    private static byte[] RenderAttachParams(string targetId)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("targetId"u8, targetId);
            w.WriteBoolean("flatten"u8, true);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private sealed class PageSession
    {
        public PageSession(DevToolsSession session)
        {
            Session = session;
        }

        public DevToolsSession Session { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public PointerState Pointer { get; } = new();

        public string? CdpSessionId { get; set; }
    }
}
