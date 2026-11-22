using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.CompanySetup.Avalonia;

/// <summary>
/// The create-company screen's body. Controls carry stable automation ids (see <see cref="Ids"/>) so a headless test or the smoke
/// harness can find and drive them. Nothing is focused when it opens (doc 27); Tab reaches each control in order.
/// </summary>
public static class CompanySetupView
{
    /// <summary>Automation ids of the controls, for tests and the smoke harness.</summary>
    public static class Ids
    {
        public const string Message = "company-message";
        public const string Name = "company-name";
        public const string Slug = "company-slug";
        public const string Owner = "company-owner";
        public const string AgentName = "company-agent-name";
        public const string AgentPurpose = "company-agent-purpose";
        public const string Locale = "company-locale";
        public const string Timezone = "company-timezone";
        public const string KekProvider = "company-kek-provider";
        public const string KekReference = "company-kek-reference";
        public const string NoBackup = "company-no-backup";
        public const string AcceptConstitution = "company-accept-constitution";
        public const string BootstrapCode = "company-bootstrap-code";
        public const string Check = "company-check";
        public const string PlanHash = "company-plan-hash";
        public const string Apply = "company-apply";
        public const string Edit = "company-edit";
        public const string Issues = "company-issues";
    }

    public static StackPanel Create(CompanySetupViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(message, Ids.Message);
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);

        var name = Field("Company name", Ids.Name, text => model.CompanyName = text);
        var slug = Field("Company identifier (cannot change later)", Ids.Slug, text => model.Slug = text);
        var owner = Field("Owner display name", Ids.Owner, text => model.OwnerName = text);
        var agentName = Field("First agent name", Ids.AgentName, text => model.AgentName = text);
        var agentPurpose = Field("First agent purpose (optional)", Ids.AgentPurpose, text => model.AgentPurpose = text);
        var locale = Field("Default language", Ids.Locale, text => model.Locale = text);
        var timezone = Field("Default time zone", Ids.Timezone, text => model.Timezone = text);
        var kekProvider = Field("Key storage provider (dpapi, systemd-creds, file or env)", Ids.KekProvider, text => model.KekProvider = text);
        var kekReference = Field("Key storage reference", Ids.KekReference, text => model.KekReference = text);
        var code = Field("Setup code from the server console", Ids.BootstrapCode, text => model.BootstrapCode = text);
        code.Box.PasswordChar = '•';

        var noBackup = new CheckBox { Content = "I understand no backup target is set yet" };
        AutomationProperties.SetAutomationId(noBackup, Ids.NoBackup);
        noBackup.IsCheckedChanged += (_, _) => model.AcknowledgeNoBackup = noBackup.IsChecked == true;

        var accept = new CheckBox { Content = "I accept the company Constitution, so the first agent can start" };
        AutomationProperties.SetAutomationId(accept, Ids.AcceptConstitution);
        accept.IsCheckedChanged += (_, _) => model.AcceptConstitution = accept.IsChecked == true;

        var check = Button("Check the plan", Ids.Check, model.CheckCommand);
        var planHash = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(planHash, Ids.PlanHash);
        AutomationProperties.SetName(planHash, "Plan hash");
        var apply = Button("Create company", Ids.Apply, model.ApplyCommand);
        var edit = Button("Edit", Ids.Edit, model.EditCommand);
        var issues = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(issues, Ids.Issues);

        var fields = new Control[] { name.Panel, slug.Panel, owner.Panel, agentName.Panel, agentPurpose.Panel, locale.Panel, timezone.Panel, kekProvider.Panel, kekReference.Panel, code.Panel, noBackup, accept, check };
        var review = new Control[] { planHash, apply, edit };

        void Refresh()
        {
            message.Text = model.Message;
            var editing = model.Phase is CompanySetupPhase.Form or CompanySetupPhase.Checking;
            var reviewing = model.Phase is CompanySetupPhase.Review or CompanySetupPhase.Applying;
            foreach (var control in fields) control.IsVisible = editing;
            foreach (var control in review) control.IsVisible = reviewing;
            foreach (var box in new[] { name.Box, slug.Box, owner.Box, agentName.Box, agentPurpose.Box, locale.Box, timezone.Box, kekProvider.Box, kekReference.Box, code.Box }) box.IsEnabled = model.CanEditForm;
            noBackup.IsEnabled = accept.IsEnabled = model.CanEditForm;
            Sync(slug.Box, model.Slug);
            Sync(agentName.Box, model.AgentName);
            Sync(kekProvider.Box, model.KekProvider);
            Sync(kekReference.Box, model.KekReference);
            Sync(timezone.Box, model.Timezone);
            Sync(locale.Box, model.Locale);
            planHash.Text = model.PlanHash is null ? "" : "Plan hash: " + model.PlanHash;
            issues.Text = string.Join(Environment.NewLine, model.Issues.Select(i => $"{i.Severity}: {i.Path} {i.Message}".Trim()));
            issues.IsVisible = model.Issues.Count > 0;
        }

        model.PropertyChanged += (_, _) => Refresh();
        Refresh();

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "Create your company", FontWeight = FontWeight.SemiBold, FontSize = 20 });
        body.Children.Add(message);
        foreach (var control in fields) body.Children.Add(control);
        foreach (var control in review) body.Children.Add(control);
        body.Children.Add(issues);
        return body;
    }

    // Writes the model's value into a box only when it differs, so typing is never overwritten and a derived slug still shows.
    private static void Sync(TextBox box, string value)
    {
        if (!string.Equals(box.Text ?? "", value, StringComparison.Ordinal)) box.Text = value;
    }

    private static (StackPanel Panel, TextBox Box) Field(string label, string id, Action<string> set)
    {
        var box = new TextBox();
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, label);
        box.GetObservable(TextBox.TextProperty).Subscribe(new Observer(text => set(text ?? "")));
        var panel = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label }, box } };
        return (panel, box);
    }

    private static Button Button(string text, string id, System.Windows.Input.ICommand command)
    {
        var button = new Button { Content = text, Command = command, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    // The first value is the box's starting state, not something the person typed, so it is skipped.
    private sealed class Observer(Action<string?> onNext) : IObserver<string?>
    {
        private bool _first = true;

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(string? value)
        {
            if (_first)
            {
                _first = false;
                return;
            }

            onNext(value);
        }
    }
}
