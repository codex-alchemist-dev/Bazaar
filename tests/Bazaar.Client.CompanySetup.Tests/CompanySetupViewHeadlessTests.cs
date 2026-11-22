using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.CompanySetup.Avalonia;

namespace Bazaar.Client.CompanySetup.Tests;

public class CompanySetupViewHeadlessTests
{
    private static T Find<T>(Control root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    [AvaloniaFact]
    public async Task The_form_is_filled_by_its_controls_checked_and_applied()
    {
        var service = new FakeSetupService();
        var model = new CompanySetupViewModel(service, new FakeDefaults());
        var window = new Window { Content = CompanySetupView.Create(model) };
        window.Show();

        Assert.Equal("dpapi", Find<TextBox>(window, CompanySetupView.Ids.KekProvider).Text);
        Assert.False(Find<Button>(window, CompanySetupView.Ids.Apply).IsVisible);

        Find<TextBox>(window, CompanySetupView.Ids.Name).Text = "Example Studio";
        Assert.Equal("example-studio", Find<TextBox>(window, CompanySetupView.Ids.Slug).Text);
        Find<TextBox>(window, CompanySetupView.Ids.Owner).Text = "Dana";
        Find<TextBox>(window, CompanySetupView.Ids.BootstrapCode).Text = "code-123";
        Find<CheckBox>(window, CompanySetupView.Ids.NoBackup).IsChecked = true;
        Assert.True(model.CheckCommand.CanExecute(null));

        model.CheckCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.True(Find<Button>(window, CompanySetupView.Ids.Apply).IsVisible);
        Assert.False(Find<Button>(window, CompanySetupView.Ids.Check).IsVisible);
        Assert.Contains(new string('a', 64), Find<SelectableTextBlock>(window, CompanySetupView.Ids.PlanHash).Text);

        model.ApplyCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("Your company is created.", Find<TextBlock>(window, CompanySetupView.Ids.Message).Text);
    }

    [AvaloniaFact]
    public async Task Server_findings_show_under_the_form()
    {
        var service = new FakeSetupService
        {
            Validation = new SetupValidation(SetupCallOutcome.Invalid, null, [new SetupIssue("error", "install.companySlug", "must be lower-case.")], "Fix it."),
        };
        var model = new CompanySetupViewModel(service, new FakeDefaults());
        var window = new Window { Content = CompanySetupView.Create(model) };
        window.Show();
        model.CompanyName = "X";
        model.OwnerName = "Y";
        model.AcknowledgeNoBackup = true;

        model.CheckCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var issues = Find<TextBlock>(window, CompanySetupView.Ids.Issues);
        Assert.True(issues.IsVisible);
        Assert.Contains("install.companySlug", issues.Text);
    }
}
