using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Android;

/// <summary>
/// The only surface the Android head must implement over <c>org.chromium.net.CronetEngine</c> and
/// <c>BidirectionalStream</c> (doc 90 section 9). It mirrors Cronet's callback model one to one and holds no logic:
/// copying to and from direct <c>ByteBuffer</c>s and the executor thread are the head's business.
/// </summary>
public interface IJavaCronetBridge
{
    /// <summary>False when Cronet is missing, disabled or the Play services provider is absent on this device.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Raised for device-level network changes (a <c>ConnectivityManager.NetworkCallback</c> in the head): handover,
    /// suspend (Doze, no connectivity) and resume. May fire on any thread.
    /// </summary>
    event Action<WireNetworkEvent>? NetworkEvent;

    /// <summary>
    /// Builds and starts a bidirectional stream (<c>newBidirectionalStreamBuilder</c> then <c>start()</c>), with early
    /// data off. Every callback reaches <paramref name="callback"/>; exactly one terminal callback
    /// (<see cref="IJavaCronetCallback.OnSucceeded"/>, <see cref="IJavaCronetCallback.OnFailed"/> or
    /// <see cref="IJavaCronetCallback.OnCanceled"/>) follows.
    /// </summary>
    IJavaCronetCall Start(CronetStreamRequest request, IJavaCronetCallback callback);
}

/// <summary>A started Java <c>BidirectionalStream</c>. Cronet allows one read and one write in flight at a time.</summary>
public interface IJavaCronetCall
{
    /// <summary>
    /// <c>read(ByteBuffer)</c> of at most <paramref name="maxBytes"/>. Completes through
    /// <see cref="IJavaCronetCallback.OnReadCompleted"/>. Pull-based: nothing is read until asked, which is the
    /// backpressure.
    /// </summary>
    void RequestRead(int maxBytes);

    /// <summary>
    /// <c>write(ByteBuffer, endOfStream)</c> then <c>flush()</c>. The head copies <paramref name="data"/> before returning
    /// or keeps it valid until <see cref="IJavaCronetCallback.OnWriteCompleted"/>. Empty data with
    /// <paramref name="endOfStream"/> half-closes the request side.
    /// </summary>
    void Write(ReadOnlyMemory<byte> data, bool endOfStream);

    /// <summary><c>cancel()</c>. Idempotent; ends in <see cref="IJavaCronetCallback.OnCanceled"/>.</summary>
    void Cancel();
}

/// <summary>Cronet's <c>BidirectionalStream.Callback</c>, with Java types replaced by .NET ones.</summary>
public interface IJavaCronetCallback
{
    /// <summary><c>onStreamReady</c>: request headers went out.</summary>
    void OnStreamReady();

    /// <summary><c>onResponseHeadersReceived</c>. <paramref name="negotiatedProtocol"/> is <c>getNegotiatedProtocol()</c> (<c>h3</c>, <c>h2</c>, ...).</summary>
    void OnResponseHeaders(int httpStatus, string negotiatedProtocol, IReadOnlyDictionary<string, string> headers);

    /// <summary><c>onReadCompleted</c>: <paramref name="data"/> is a copy the adapter owns.</summary>
    void OnReadCompleted(ReadOnlyMemory<byte> data, bool endOfStream);

    /// <summary><c>onWriteCompleted</c>.</summary>
    void OnWriteCompleted();

    /// <summary><c>onResponseTrailersReceived</c>.</summary>
    void OnTrailers(IReadOnlyDictionary<string, string> trailers);

    /// <summary><c>onSucceeded</c>: terminal, everything was delivered.</summary>
    void OnSucceeded();

    /// <summary><c>onFailed</c>: terminal.</summary>
    void OnFailed(Exception failure);

    /// <summary><c>onCanceled</c>: terminal.</summary>
    void OnCanceled();
}
