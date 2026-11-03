using Bazaar.Server.Security.Packages;

namespace Bazaar.Server.Tests.Security.Packages;

public class StatusListenerPublisherTests
{
    private static readonly Guid _request = Guid.Parse("0196a6f0-0000-7000-8000-000000000501");

    private sealed class RecordingListener(string name, List<string> log) : IPackageRequestStatusListener
    {
        public void OnStatusChanged(Guid requestId, PackageRequestStatus status) =>
            log.Add($"{name}:{requestId}:{status}");
    }

    private sealed class ThrowingListener : IPackageRequestStatusListener
    {
        public void OnStatusChanged(Guid requestId, PackageRequestStatus status) =>
            throw new InvalidOperationException("listener failed");
    }

    private sealed class RecordingSignInListener(List<string> log) : ISignInStatusListener
    {
        public void OnStatusChanged(string serviceId, SignInStatus status) => log.Add($"{serviceId}:{status}");
    }

    [Fact]
    public void Listeners_are_called_in_subscription_order()
    {
        var log = new List<string>();
        var publisher = new PackageRequestStatusPublisher((_, _) => { });
        publisher.Subscribe(new RecordingListener("first", log));
        publisher.Subscribe(new RecordingListener("second", log));
        publisher.Subscribe(new RecordingListener("third", log));

        publisher.Publish(_request, PackageRequestStatus.AwaitingOwner);
        publisher.Publish(_request, PackageRequestStatus.Approved);

        Assert.Equal(
            [
                $"first:{_request}:AwaitingOwner",
                $"second:{_request}:AwaitingOwner",
                $"third:{_request}:AwaitingOwner",
                $"first:{_request}:Approved",
                $"second:{_request}:Approved",
                $"third:{_request}:Approved",
            ],
            log);
    }

    [Fact]
    public void A_throwing_listener_is_reported_and_the_others_still_run()
    {
        var log = new List<string>();
        var errors = new List<string>();
        var publisher = new PackageRequestStatusPublisher((listener, ex) => errors.Add($"{listener}:{ex.Message}"));
        publisher.Subscribe(new RecordingListener("before", log));
        publisher.Subscribe(new ThrowingListener());
        publisher.Subscribe(new RecordingListener("after", log));

        publisher.Publish(_request, PackageRequestStatus.Installed);

        Assert.Equal(
            [$"before:{_request}:Installed", $"after:{_request}:Installed"],
            log);
        var error = Assert.Single(errors);
        Assert.Contains(nameof(ThrowingListener), error, StringComparison.Ordinal);
        Assert.EndsWith(":listener failed", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_publish_with_no_listeners_does_nothing()
    {
        var errors = new List<string>();
        var publisher = new PackageRequestStatusPublisher((listener, _) => errors.Add(listener));

        publisher.Publish(_request, PackageRequestStatus.Requested);

        Assert.Empty(errors);
    }

    [Fact]
    public void Sign_in_status_follows_the_same_shape()
    {
        var log = new List<string>();
        var publisher = new SignInStatusPublisher((_, _) => { });
        publisher.Subscribe(new RecordingSignInListener(log));
        publisher.Subscribe(new RecordingSignInListener(log));

        publisher.Publish("github-app", SignInStatus.ReauthRequired);

        Assert.Equal(["github-app:ReauthRequired", "github-app:ReauthRequired"], log);
    }

    [Fact]
    public void A_null_listener_is_refused()
    {
        var publisher = new PackageRequestStatusPublisher((_, _) => { });

        Assert.Throws<ArgumentNullException>(() => publisher.Subscribe(null!));
    }
}
