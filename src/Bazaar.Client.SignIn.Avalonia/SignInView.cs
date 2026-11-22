using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.SignIn.Avalonia;

/// <summary>
/// The sign-in screen's body. Controls carry stable automation ids (see <see cref="Ids"/>) so a headless test or the smoke harness
/// can find and drive them. Nothing is focused when it opens (doc 27, nothing focused by default); Tab reaches each control in order.
/// </summary>
public static class SignInView
{
    /// <summary>Automation ids of the controls, for tests and the smoke harness.</summary>
    public static class Ids
    {
        public const string Message = "signin-message";
        public const string Retry = "signin-retry";
        public const string OpenPage = "signin-open-page";
        public const string PageAddress = "signin-page-address";
        public const string Code = "signin-code";
        public const string Submit = "signin-submit";
        public const string SignOut = "signin-sign-out";
    }

    public static StackPanel Create(SignInViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(message, Ids.Message);
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);

        var retry = Button("Retry", Ids.Retry, model.RetryCommand);
        var open = Button("Open sign-in page", Ids.OpenPage, model.OpenSignInPageCommand);
        var address = new SelectableTextBlock { Text = model.SignInPageAddress.ToString(), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(address, Ids.PageAddress);
        AutomationProperties.SetName(address, "Sign-in page address");

        var code = new TextBox { PlaceholderText = "Sign-in code", PasswordChar = '\0' };
        AutomationProperties.SetAutomationId(code, Ids.Code);
        AutomationProperties.SetName(code, "Sign-in code");
        code.GetObservable(TextBox.TextProperty).Subscribe(new Observer(text => model.CodeText = text ?? ""));
        var submit = Button("Sign in", Ids.Submit, model.SubmitCodeCommand);
        var signOut = Button("Sign out", Ids.SignOut, model.SignOutCommand);

        void Refresh()
        {
            message.Text = model.Message;
            var phase = model.Phase;
            retry.IsVisible = phase is SignInPhase.Unreachable or SignInPhase.NotReady;
            open.IsVisible = address.IsVisible = code.IsVisible = submit.IsVisible = phase is SignInPhase.SignedOut or SignInPhase.SigningIn;
            code.IsEnabled = model.CanEnterCode;
            signOut.IsVisible = phase == SignInPhase.SignedIn;
            if (model.CodeText.Length == 0 && code.Text is { Length: > 0 }) code.Text = "";
        }

        model.PropertyChanged += (_, _) => Refresh();
        Refresh();

        return new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = "Sign in", FontWeight = FontWeight.SemiBold, FontSize = 20 }, message, retry, open, address, code, submit, signOut },
        };
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
