using System.Net;
using System.Text;

namespace Bazaar.Client.SignIn.Tests;

/// <summary>Answers each request from a function and records what was sent. No network.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(string Method, string Path, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
        return answer(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FakeLauncher(bool opens = true) : IUrlLauncher
{
    public List<Uri> Opened { get; } = [];

    public Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default)
    {
        Opened.Add(address);
        return Task.FromResult(opens);
    }
}

internal sealed class FakeService : ISignInService
{
    public ServerReachability Reachability { get; set; } = ServerReachability.Ready;
    public ClientSession? Session { get; set; }
    public SignInResult RedeemResult { get; set; } = new(SignInOutcome.SignedIn, "Signed in.");
    public SignInResult RefreshResult { get; set; } = new(SignInOutcome.Refused, "no");
    public List<string> Redeemed { get; } = [];
    public Uri SignInPageAddress { get; } = new("http://127.0.0.1:8080/signin?audience=native");

    public Task<ServerReachability> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Reachability);

    public Task<SignInResult> RedeemHandoffAsync(string code, CancellationToken cancellationToken = default)
    {
        Redeemed.Add(code);
        return Task.FromResult(RedeemResult);
    }

    public Task<SignInResult> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(RefreshResult);

    public Task<ClientSession?> CurrentSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        Session = null;
        return Task.CompletedTask;
    }
}
