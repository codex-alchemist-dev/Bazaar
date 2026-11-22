namespace Bazaar.Client.CompanySetup.Tests;

public class CompanySetupViewModelTests
{
    private static (CompanySetupViewModel Model, FakeSetupService Service) Make(IKeyCustodyDefaults? defaults = null, Func<Task<string?>>? bearer = null)
    {
        var service = new FakeSetupService();
        return (new CompanySetupViewModel(service, defaults ?? new FakeDefaults(), bearer), service);
    }

    private static void Fill(CompanySetupViewModel model)
    {
        model.CompanyName = "Example Studio";
        model.OwnerName = "Dana";
        model.BootstrapCode = "code-123";
        model.AcknowledgeNoBackup = true;
    }

    [Fact]
    public void The_slug_follows_the_name_until_the_person_edits_it()
    {
        var (model, _) = Make();

        model.CompanyName = "Codex Alchemist";
        Assert.Equal("codex-alchemist", model.Slug);

        model.Slug = "my-own";
        model.CompanyName = "Something Else";
        Assert.Equal("my-own", model.Slug);
    }

    [Fact]
    public void Key_storage_is_prefilled_from_this_machines_default_and_blank_when_there_is_none()
    {
        var (withDefault, _) = Make();
        Assert.Equal("dpapi", withDefault.KekProvider);
        Assert.Equal("dpapi:keys/kek.bin", withDefault.KekReference);

        var (none, _) = Make(new FakeDefaults(null, ""));
        Assert.Equal("", none.KekProvider);
        none.CompanyName = "X";
        none.OwnerName = "Y";
        Assert.False(none.CheckCommand.CanExecute(null));
    }

    [Fact]
    public void Check_is_offered_only_when_every_field_is_filled()
    {
        var (model, _) = Make();
        Assert.False(model.CheckCommand.CanExecute(null));

        Fill(model);
        Assert.True(model.CheckCommand.CanExecute(null));

        model.OwnerName = " ";
        Assert.False(model.CheckCommand.CanExecute(null));

        model.OwnerName = "Dana";
        model.AcknowledgeNoBackup = false;
        Assert.False(model.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_valid_check_shows_the_hash_and_apply_runs_only_that_hash()
    {
        var (model, service) = Make();
        Fill(model);

        model.CheckCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(CompanySetupPhase.Review, model.Phase);
        Assert.Equal(new string('a', 64), model.PlanHash);
        Assert.False(model.CheckCommand.CanExecute(null));
        Assert.True(model.ApplyCommand.CanExecute(null));
        var (request, access) = Assert.Single(service.Validated);
        Assert.Equal("example-studio", request.CompanySlug);
        Assert.Equal("code-123", access.BootstrapCode);

        var raised = 0;
        model.Completed += (_, _) => raised++;
        model.ApplyCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(CompanySetupPhase.Done, model.Phase);
        Assert.Equal(1, raised);
        Assert.Equal(new string('a', 64), Assert.Single(service.Applied).Hash);
    }

    [Fact]
    public async Task An_invalid_plan_stays_on_the_form_and_shows_the_servers_findings()
    {
        var (model, service) = Make();
        service.Validation = new SetupValidation(SetupCallOutcome.Invalid, null, [new SetupIssue("error", "install.companySlug", "must be lower-case.")], "The plan has 1 problem(s). Fix them and check again.");
        Fill(model);

        model.CheckCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(CompanySetupPhase.Form, model.Phase);
        Assert.Null(model.PlanHash);
        Assert.Equal("install.companySlug", Assert.Single(model.Issues).Path);
        Assert.Contains("problem", model.Message);
        Assert.False(model.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Edit_returns_from_review_to_the_form_and_drops_nothing_the_person_typed()
    {
        var (model, _) = Make();
        Fill(model);
        model.CheckCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        model.EditCommand.Execute(null);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(CompanySetupPhase.Form, model.Phase);
        Assert.Equal("Example Studio", model.CompanyName);
    }

    [Theory]
    [InlineData(SetupCallOutcome.Held)]
    [InlineData(SetupCallOutcome.ApplyFailed)]
    [InlineData(SetupCallOutcome.AccessRefused)]
    public async Task A_held_failed_or_refused_apply_goes_back_to_the_form_with_the_reason(SetupCallOutcome outcome)
    {
        var (model, service) = Make();
        service.Application = new SetupApplication(outcome, [], [], false, "Not applied.");
        Fill(model);
        model.CheckCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        model.ApplyCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(CompanySetupPhase.Form, model.Phase);
        Assert.Equal("Not applied.", model.Message);
        Assert.Null(model.PlanHash);
    }

    [Fact]
    public async Task The_owners_bearer_token_is_sent_once_there_is_one()
    {
        var (model, service) = Make(bearer: () => Task.FromResult<string?>("owner-token"));
        Fill(model);
        model.BootstrapCode = "";

        model.CheckCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var (_, access) = Assert.Single(service.Validated);
        Assert.Null(access.BootstrapCode);
        Assert.Equal("owner-token", access.BearerToken);
    }

    [Fact]
    public async Task A_replayed_apply_says_the_company_already_exists()
    {
        var (model, service) = Make();
        service.Application = new SetupApplication(SetupCallOutcome.Ok, [], [], true, "ok");
        Fill(model);
        model.CheckCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        model.ApplyCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(CompanySetupPhase.Done, model.Phase);
        Assert.Contains("already", model.Message);
    }
}
