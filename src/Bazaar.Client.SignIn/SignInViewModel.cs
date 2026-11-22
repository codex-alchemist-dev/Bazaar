using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Bazaar.Client.SignIn;

/// <summary>Where the sign-in screen is.</summary>
public enum SignInPhase
{
    /// <summary>Asking the server whether it is up.</summary>
    Connecting,

    /// <summary>The server did not answer. Retry is offered.</summary>
    Unreachable,

    /// <summary>The server answered but is not ready. Retry is offered.</summary>
    NotReady,

    /// <summary>The server is up and this device has no session. The page and the code box are offered.</summary>
    SignedOut,

    /// <summary>A code is being redeemed.</summary>
    SigningIn,

    /// <summary>This device holds a session.</summary>
    SignedIn,
}

/// <summary>
/// The sign-in screen (doc 45 section 2.6). The person signs in on the server's own page in their browser, which hands back a
/// single-use code; this screen takes the code, by paste or by the <c>bazaar://signin</c> address the operating system forwards.
/// The client never sees a password or a passkey. Every state is reachable by keyboard and has a text message for a screen reader.
/// </summary>
public sealed class SignInViewModel : INotifyPropertyChanged
{
    private readonly ISignInService _service;
    private readonly IUrlLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly ClientCommand _retry;
    private readonly ClientCommand _openPage;
    private readonly ClientCommand _submit;
    private readonly ClientCommand _signOut;
    private SignInPhase _phase = SignInPhase.Connecting;
    private string _message = "Connecting to your company server.";
    private string _codeText = "";

    public SignInViewModel(ISignInService service, IUrlLauncher launcher, TimeProvider? time = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _time = time ?? TimeProvider.System;
        _retry = new ClientCommand(StartAsync, () => Phase is SignInPhase.Unreachable or SignInPhase.NotReady);
        _openPage = new ClientCommand(OpenPageAsync, () => Phase == SignInPhase.SignedOut);
        _submit = new ClientCommand(() => SubmitAsync(CodeText), () => Phase == SignInPhase.SignedOut && HandoffCode.TryRead(CodeText) is not null);
        _signOut = new ClientCommand(SignOutAsync, () => Phase == SignInPhase.SignedIn);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised once when this device ends up holding a session, whether freshly signed in or already signed in.</summary>
    public event EventHandler? SignedIn;

    public SignInPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(nameof(CanEnterCode));
            RaiseCanExecute();
        }
    }

    /// <summary>One plain sentence saying what is happening or what to do next.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>What the person pasted. Not stored anywhere.</summary>
    public string CodeText
    {
        get => _codeText;
        set
        {
            if (Set(ref _codeText, value ?? "")) _submit.Changed();
        }
    }

    public bool CanEnterCode => Phase == SignInPhase.SignedOut;

    /// <summary>The server's sign-in page, shown so the person can open it by hand if no browser launches.</summary>
    public Uri SignInPageAddress => _service.SignInPageAddress;

    public ICommand RetryCommand => _retry;

    public ICommand OpenSignInPageCommand => _openPage;

    public ICommand SubmitCodeCommand => _submit;

    public ICommand SignOutCommand => _signOut;

    /// <summary>Probes the server, then reuses a saved session if there is one. Call once when the screen opens; Retry calls it again.</summary>
    public async Task StartAsync()
    {
        Phase = SignInPhase.Connecting;
        Message = "Connecting to your company server.";

        switch (await _service.ProbeAsync())
        {
            case ServerReachability.Unreachable:
                Phase = SignInPhase.Unreachable;
                Message = "Cannot reach your company server. Check that it is running, then choose Retry.";
                return;
            case ServerReachability.NotReady:
                Phase = SignInPhase.NotReady;
                Message = "The server is still starting. Choose Retry in a moment.";
                return;
        }

        var saved = await _service.CurrentSessionAsync();
        if (saved is not null)
        {
            if (saved.AccessTokenExpiresAt > _time.GetUtcNow())
            {
                Finish();
                return;
            }

            if ((await _service.RefreshAsync()).Succeeded)
            {
                Finish();
                return;
            }
        }

        Phase = SignInPhase.SignedOut;
        Message = "Open the sign-in page, sign in, then paste the code it gives you here.";
    }

    /// <summary>
    /// Takes the address the operating system forwarded when the sign-in page redirected to <c>bazaar://signin?code=...</c>. The
    /// code is redeemed straight away, with no paste. Ignored unless the screen is waiting for a code.
    /// </summary>
    public async Task AcceptLaunchAddressAsync(string address)
    {
        if (Phase != SignInPhase.SignedOut || HandoffCode.TryRead(address) is not { } code) return;
        await SubmitAsync(code);
    }

    private async Task OpenPageAsync()
    {
        var opened = await _launcher.OpenAsync(SignInPageAddress);
        Message = opened
            ? "The sign-in page is open in your browser. Paste the code it gives you here."
            : "Your browser did not open. Go to the sign-in page address shown below, then paste the code here.";
    }

    private async Task SubmitAsync(string text)
    {
        Phase = SignInPhase.SigningIn;
        Message = "Signing in.";
        var result = await _service.RedeemHandoffAsync(text);
        CodeText = "";
        if (result.Succeeded)
        {
            Finish();
            return;
        }

        Phase = SignInPhase.SignedOut;
        Message = result.Message;
    }

    private async Task SignOutAsync()
    {
        await _service.SignOutAsync();
        Phase = SignInPhase.SignedOut;
        Message = "Signed out. Open the sign-in page to sign in again.";
    }

    private void Finish()
    {
        Phase = SignInPhase.SignedIn;
        Message = "You are signed in.";
        SignedIn?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseCanExecute()
    {
        _retry.Changed();
        _openPage.Changed();
        _submit.Changed();
        _signOut.Changed();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
