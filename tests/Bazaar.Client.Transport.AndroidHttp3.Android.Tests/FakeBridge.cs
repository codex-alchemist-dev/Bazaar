using Bazaar.Client.Transport.AndroidHttp3;
using Bazaar.Client.Transport.AndroidHttp3.Android;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Android.Tests;

internal sealed class FakeBridge : IJavaCronetBridge
{
    public bool IsAvailable { get; set; } = true;

    public FakeStream? Stream { get; private set; }

    public bool AutoReady { get; set; } = true;

    public event Action<WireNetworkEvent>? NetworkEvent;

    public void Raise(WireNetworkEvent e) => NetworkEvent?.Invoke(e);

    public int NetworkSubscribers => NetworkEvent?.GetInvocationList().Length ?? 0;

    public IJavaCronetCall Start(CronetStreamRequest request, IJavaCronetCallback callback)
    {
        Stream = new FakeStream(callback);
        if (AutoReady) callback.OnStreamReady();
        return Stream;
    }
}

internal sealed class FakeStream(IJavaCronetCallback callback) : IJavaCronetCall
{
    public IJavaCronetCallback Callback { get; } = callback;

    public List<int> ReadRequests { get; } = [];

    public List<(byte[] Data, bool End)> Writes { get; } = [];

    public int Cancels { get; private set; }

    public bool CompleteWritesImmediately { get; set; } = true;

    public void RequestRead(int maxBytes) => ReadRequests.Add(maxBytes);

    public void Write(ReadOnlyMemory<byte> data, bool endOfStream)
    {
        Writes.Add((data.ToArray(), endOfStream));
        if (CompleteWritesImmediately) Callback.OnWriteCompleted();
    }

    public void Cancel()
    {
        Cancels++;
        Callback.OnCanceled();
    }
}
