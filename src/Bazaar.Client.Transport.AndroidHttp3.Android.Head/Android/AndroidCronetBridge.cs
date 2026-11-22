using Android.Content;
using Android.Net;
using Bazaar.Wire.Abstractions;
using Java.Nio;
using Java.Util.Concurrent;
using Org.Chromium.Net;

[assembly: Android.App.UsesPermission(Name = "android.permission.INTERNET")]
[assembly: Android.App.UsesPermission(Name = "android.permission.ACCESS_NETWORK_STATE")]

namespace Bazaar.Client.Transport.AndroidHttp3.Android.Head;

/// <summary>
/// <see cref="IJavaCronetBridge"/> over the embedded Java Cronet library (<c>org.chromium.net:cronet-embedded</c>), doc 90
/// section 9. It holds no logic of its own beyond Java glue: one engine with QUIC and HTTP/2 on, one stream per
/// <see cref="Start"/>, a single-thread executor for Cronet callbacks, and a default-network callback that feeds the
/// platform-neutral <see cref="CronetNetworkTracker"/>. Written against the generated binding; not run on a device.
/// </summary>
public sealed class AndroidCronetBridge : IJavaCronetBridge, IDisposable
{
    private readonly CronetEngine? _engine;
    private readonly IExecutorService _executor;
    private readonly CronetNetworkTracker _tracker = new();
    private readonly ConnectivityManager? _connectivity;
    private readonly NetworkWatcher? _watcher;

    /// <summary>Builds the engine (QUIC and HTTP/2 on, no disk cache) and starts watching the default network.</summary>
    public AndroidCronetBridge(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _executor = Executors.NewSingleThreadExecutor()!;
        try
        {
            _engine = new CronetEngine.Builder(context.ApplicationContext)
                .EnableHttp2(true)!
                .EnableQuic(true)!
                .EnableBrotli(false)!
                .Build();
        }
        catch (Java.Lang.Exception)
        {
            _engine = null;
        }

        _connectivity = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
        if (_connectivity is not null)
        {
            _watcher = new NetworkWatcher(this);
            _connectivity.RegisterDefaultNetworkCallback(_watcher);
        }
    }

    /// <inheritdoc />
    public bool IsAvailable => _engine is not null;

    /// <inheritdoc />
    public event Action<WireNetworkEvent>? NetworkEvent;

    /// <inheritdoc />
    public IJavaCronetCall Start(CronetStreamRequest request, IJavaCronetCallback callback)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callback);
        var engine = _engine ?? throw new InvalidOperationException("Cronet is not available on this device.");
        var headers = CronetHeaderMapper.ForRequest(request.Headers);

        var bridgeCallback = new StreamCallback(callback);
        var builder = engine.NewBidirectionalStreamBuilder(request.Url.AbsoluteUri, bridgeCallback, _executor)!
            .SetHttpMethod("POST")!
            .DelayRequestHeadersUntilFirstFlush(true)!;
        foreach (var (name, value) in headers) builder = builder.AddHeader(name, value)!;

        var stream = builder.Build()!;
        var call = new Call(stream);
        stream.Start();
        return call;
    }

    /// <summary>Stops watching the network and shuts the engine and executor down.</summary>
    public void Dispose()
    {
        if (_watcher is not null) _connectivity?.UnregisterNetworkCallback(_watcher);
        _watcher?.Dispose();
        _engine?.Shutdown();
        _executor.Shutdown();
    }

    private void Report(WireNetworkEvent? networkEvent)
    {
        if (networkEvent is not null) NetworkEvent?.Invoke(networkEvent);
    }

    private sealed class Call(BidirectionalStream stream) : IJavaCronetCall
    {
        // Cronet allows one read in flight; the adapter guarantees it and requests reads only when the consumer asks.
        private ByteBuffer? _readBuffer;
        private ByteBuffer? _writeBuffer;

        public void RequestRead(int maxBytes)
        {
            _readBuffer = ByteBuffer.AllocateDirect(maxBytes)!;
            stream.Read(_readBuffer);
        }

        public void Write(ReadOnlyMemory<byte> data, bool endOfStream)
        {
            // Cronet needs a direct buffer that stays valid until onWriteCompleted; this copy is that buffer.
            _writeBuffer = ByteBuffer.AllocateDirect(data.Length)!;
            if (!data.IsEmpty)
            {
                _writeBuffer.Put(data.ToArray());
            }

            _writeBuffer.Flip();
            stream.Write(_writeBuffer, endOfStream);
            stream.Flush();
        }

        public void Cancel() => stream.Cancel();
    }

    private sealed class StreamCallback(IJavaCronetCallback sink) : BidirectionalStream.Callback
    {
        public override void OnStreamReady(BidirectionalStream? stream) => sink.OnStreamReady();

        public override void OnResponseHeadersReceived(BidirectionalStream? stream, UrlResponseInfo? info)
        {
            if (info is null) return;
            sink.OnResponseHeaders(info.HttpStatusCode, info.NegotiatedProtocol ?? string.Empty, CronetHeaderMapper.ForResponse(Flatten(info.AllHeadersAsList)));
        }

        public override void OnReadCompleted(BidirectionalStream? stream, UrlResponseInfo? info, ByteBuffer? buffer, bool endOfStream)
        {
            if (buffer is null)
            {
                sink.OnReadCompleted(ReadOnlyMemory<byte>.Empty, endOfStream);
                return;
            }

            buffer.Flip();
            var bytes = new byte[buffer.Remaining()];
            buffer.Get(bytes);
            sink.OnReadCompleted(bytes, endOfStream);
        }

        public override void OnWriteCompleted(BidirectionalStream? stream, UrlResponseInfo? info, ByteBuffer? buffer, bool endOfStream) =>
            sink.OnWriteCompleted();

        public override void OnResponseTrailersReceived(BidirectionalStream? stream, UrlResponseInfo? info, UrlResponseInfo.HeaderBlock? trailers) =>
            sink.OnTrailers(CronetHeaderMapper.ForResponse(Flatten(trailers?.AsList)));

        public override void OnSucceeded(BidirectionalStream? stream, UrlResponseInfo? info) => sink.OnSucceeded();

        public override void OnFailed(BidirectionalStream? stream, UrlResponseInfo? info, CronetException? error) =>
            sink.OnFailed(CronetFailure.ToException(Describe(error)));

        public override void OnCanceled(BidirectionalStream? stream, UrlResponseInfo? info) => sink.OnCanceled();

        private static CronetFailureInfo Describe(CronetException? error) => error switch
        {
            QuicException quic => new CronetFailureInfo(CronetFailureKind.Quic, quic.ErrorCode, quic.CronetInternalErrorCode, quic.Message ?? string.Empty),
            NetworkException network => new CronetFailureInfo(CronetFailureKind.Network, network.ErrorCode, network.CronetInternalErrorCode, network.Message ?? string.Empty),
            _ => new CronetFailureInfo(CronetFailureKind.Cronet, -1, 0, error?.Message ?? "unknown failure"),
        };

        private static IEnumerable<KeyValuePair<string, string>> Flatten(IList<IDictionary<string, string>>? list)
        {
            // Cronet's AsList is List<Map.Entry<String, String>>; the binding surfaces it as a list of entries.
            if (list is null) yield break;
            foreach (var entry in list)
            {
                foreach (var pair in entry) yield return pair;
            }
        }
    }

    private sealed class NetworkWatcher(AndroidCronetBridge owner) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => owner.Report(owner._tracker.OnAvailable(network.NetworkHandle));

        public override void OnLost(Network network) => owner.Report(owner._tracker.OnLost(network.NetworkHandle));
    }
}
