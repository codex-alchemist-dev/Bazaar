using System.Net;
using System.Text;

namespace Bazaar.Client.CompanySetup.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(string Method, string Path, string? Body, HttpRequestMessage Message)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body, request));
        return answer(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FakeDefaults(string? provider = "dpapi", string reference = "dpapi:keys/kek.bin") : IKeyCustodyDefaults
{
    public string? DefaultProvider { get; } = provider;

    public string DefaultReference { get; } = reference;
}

internal sealed class FakeSetupService : ICompanySetupService
{
    public SetupValidation Validation { get; set; } = new(SetupCallOutcome.Ok, new string('a', 64), [], "ok");
    public SetupApplication Application { get; set; } = new(SetupCallOutcome.Ok, ["owner"], [], false, "ok");
    public List<(CompanySetupRequest Request, SetupAccess Access)> Validated { get; } = [];
    public List<(string Hash, SetupAccess Access)> Applied { get; } = [];

    public Task<SetupValidation> ValidateAsync(CompanySetupRequest request, SetupAccess access, CancellationToken cancellationToken = default)
    {
        Validated.Add((request, access));
        return Task.FromResult(Validation);
    }

    public Task<SetupApplication> ApplyAsync(CompanySetupRequest request, string planHash, SetupAccess access, CancellationToken cancellationToken = default)
    {
        Applied.Add((planHash, access));
        return Task.FromResult(Application);
    }
}
