using System.Net;

namespace Bazaar.Client.CompanySetup.Tests;

public class HttpCompanySetupServiceTests
{
    private static readonly CompanySetupRequest _request =
        new("Example Studio", "example-studio", "Dana", "en", "UTC", "dpapi", "dpapi:keys/kek.bin", true);

    private static (HttpCompanySetupService Service, FakeHandler Handler) Make(Func<HttpRequestMessage, string?, HttpResponseMessage> answer)
    {
        var handler = new FakeHandler(answer);
        return (new HttpCompanySetupService(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") }), handler);
    }

    private static readonly string _hash = new('a', 64);

    [Fact]
    public async Task Validate_posts_the_plan_with_the_bootstrap_code_header_and_returns_the_hash()
    {
        var (service, handler) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK, $$"""{"exitCode":0,"planHash":"{{_hash}}","issues":[]}"""));

        var result = await service.ValidateAsync(_request, new SetupAccess("code-123", null), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCallOutcome.Ok, result.Outcome);
        Assert.Equal(_hash, result.PlanHash);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/setup/plans", sent.Path);
        Assert.Contains("\"companySlug\":\"example-studio\"", sent.Body);
        Assert.Equal("code-123", Assert.Single(sent.Message.Headers.GetValues("X-Bazaar-Bootstrap-Code")));
        Assert.Null(sent.Message.Headers.Authorization);
    }

    [Fact]
    public async Task A_bearer_token_goes_in_the_authorization_header_and_the_code_is_left_out_when_blank()
    {
        var (service, handler) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK, $$"""{"exitCode":0,"planHash":"{{_hash}}","issues":[]}"""));

        await service.ValidateAsync(_request, new SetupAccess("  ", "tok"), TestContext.Current.CancellationToken);

        var sent = handler.Requests[0].Message;
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        Assert.Equal("tok", sent.Headers.Authorization.Parameter);
        Assert.False(sent.Headers.Contains("X-Bazaar-Bootstrap-Code"));
    }

    [Fact]
    public async Task Invalid_plans_return_the_servers_issues_and_no_hash()
    {
        var (service, _) = Make((_, _) => FakeHandler.Json(HttpStatusCode.UnprocessableEntity,
            """{"exitCode":1,"planHash":null,"issues":[{"severity":"error","path":"install.companySlug","message":"must be lower-case."}]}"""));

        var result = await service.ValidateAsync(_request, new SetupAccess("c", null), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCallOutcome.Invalid, result.Outcome);
        Assert.Null(result.PlanHash);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("install.companySlug", issue.Path);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refused_caller_is_told_to_check_the_setup_code(HttpStatusCode status)
    {
        var (service, _) = Make((_, _) => FakeHandler.Json(status, """{"error":"unauthorized"}"""));

        var validated = await service.ValidateAsync(_request, new SetupAccess("bad", null), TestContext.Current.CancellationToken);
        var applied = await service.ApplyAsync(_request, _hash, new SetupAccess("bad", null), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCallOutcome.AccessRefused, validated.Outcome);
        Assert.Equal(SetupCallOutcome.AccessRefused, applied.Outcome);
        Assert.Contains("setup code", validated.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_busy_or_missing_server_is_unavailable(HttpStatusCode status)
    {
        var (service, _) = Make((_, _) => FakeHandler.Json(status, """{"error":"x"}"""));

        Assert.Equal(SetupCallOutcome.Unavailable, (await service.ValidateAsync(_request, new SetupAccess("c", null), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task A_network_failure_is_unavailable_not_an_exception()
    {
        var (service, _) = Make((_, _) => throw new HttpRequestException("down"));

        Assert.Equal(SetupCallOutcome.Unavailable, (await service.ValidateAsync(_request, new SetupAccess("c", null), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(SetupCallOutcome.Unavailable, (await service.ApplyAsync(_request, _hash, new SetupAccess("c", null), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task Apply_posts_to_the_hash_route_and_reports_the_applied_steps()
    {
        var (service, handler) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK,
            $$"""{"exitCode":0,"planHash":"{{_hash}}","appliedSteps":["install","owner"],"issues":[],"replayed":true}"""));

        var result = await service.ApplyAsync(_request, _hash, new SetupAccess("c", null), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCallOutcome.Ok, result.Outcome);
        Assert.Equal(["install", "owner"], result.AppliedSteps);
        Assert.True(result.Replayed);
        Assert.Equal($"/setup/plans/{_hash}/apply", handler.Requests[0].Path);
    }

    [Theory]
    [InlineData(2, SetupCallOutcome.Held)]
    [InlineData(3, SetupCallOutcome.ApplyFailed)]
    public async Task Held_and_failed_applies_are_told_apart(int exitCode, SetupCallOutcome expected)
    {
        var (service, _) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK, $$"""{"exitCode":{{exitCode}},"planHash":"{{_hash}}","appliedSteps":[],"issues":[],"replayed":false}"""));

        Assert.Equal(expected, (await service.ApplyAsync(_request, _hash, new SetupAccess("c", null), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task An_apply_that_is_already_running_asks_to_wait()
    {
        var (service, _) = Make((_, _) => FakeHandler.Json(HttpStatusCode.Conflict, """{"error":"apply_in_progress"}"""));

        var result = await service.ApplyAsync(_request, _hash, new SetupAccess("c", null), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCallOutcome.Unavailable, result.Outcome);
        Assert.Contains("already running", result.Message);
    }

    [Fact]
    public void Access_never_prints_its_secrets() =>
        Assert.DoesNotContain("tok-", new SetupAccess("code-", "tok-").ToString());
}
