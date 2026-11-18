namespace Bazaar.Push.Tests;

/// <summary>A scripted FCM provider. Registration returns a fixed token and records each call.</summary>
public sealed class FakeFcmPushProvider : IPushProvider
{
    public string Name => "fcm";

    public int RegisterCalls { get; private set; }

    public Task<string> RegisterAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RegisterCalls++;
        return Task.FromResult("fake-fcm-installation-id");
    }
}

/// <summary>A scripted UnifiedPush provider. Registration returns a fixed endpoint and records each call.</summary>
public sealed class FakeUnifiedPushProvider : IPushProvider
{
    public string Name => "unifiedpush";

    public int RegisterCalls { get; private set; }

    public Task<string> RegisterAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RegisterCalls++;
        return Task.FromResult("https://push.example.invalid/fake-endpoint");
    }
}

/// <summary>A fetcher that returns a scripted count or throws, and records each kind it was asked to fetch.</summary>
public sealed class FakeWakeFetcher : IWakeFetcher
{
    private readonly int _newEvents;
    private readonly Exception? _failure;

    public FakeWakeFetcher(int newEvents = 0, Exception? failure = null)
    {
        _newEvents = newEvents;
        _failure = failure;
    }

    public List<WakeKind> Fetched { get; } = [];

    public Task<WakeFetchResult> FetchAsync(WakeKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Fetched.Add(kind);
        if (_failure is not null)
        {
            throw _failure;
        }

        return Task.FromResult(new WakeFetchResult(_newEvents));
    }
}

/// <summary>A notifier that records each notification it was asked to show.</summary>
public sealed class FakeLocalNotifier : ILocalNotifier
{
    public List<WakeKind> Shown { get; } = [];

    public Task ShowAsync(WakeKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Shown.Add(kind);
        return Task.CompletedTask;
    }
}
