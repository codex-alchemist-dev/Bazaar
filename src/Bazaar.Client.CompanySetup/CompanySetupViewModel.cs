using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Bazaar.Client.CompanySetup;

/// <summary>Where the create-company screen is.</summary>
public enum CompanySetupPhase
{
    /// <summary>The person is filling in the form.</summary>
    Form,

    /// <summary>The server is checking the plan.</summary>
    Checking,

    /// <summary>The plan is valid and its hash is shown. Apply is offered.</summary>
    Review,

    /// <summary>The server is applying the plan.</summary>
    Applying,

    /// <summary>The company exists.</summary>
    Done,
}

/// <summary>
/// The create-company screen (doc 45 sections 1.1 and 2.6): the same setup plan the wizard, file and API produce, entered in a
/// connected client. It checks the plan first (a dry run that writes nothing), shows the plan hash, and applies only that hash.
/// A company is one per install (doc 45), so there is nothing to choose between: a server that already has a company shows no form.
/// </summary>
public sealed class CompanySetupViewModel : INotifyPropertyChanged
{
    private readonly ICompanySetupService _service;
    private readonly Func<Task<string?>> _bearerToken;
    private readonly ClientCommand _check;
    private readonly ClientCommand _apply;
    private readonly ClientCommand _edit;
    private CompanySetupPhase _phase = CompanySetupPhase.Form;
    private string _message = "Name your company and its owner. The server checks everything before it changes anything.";
    private string _companyName = "";
    private string _slug = "";
    private bool _slugEdited;
    private string _ownerName = "";
    private string _locale = "en";
    private string _timezone;
    private string _kekProvider;
    private string _kekReference;
    private bool _acknowledgeNoBackup;
    private bool _acceptConstitution;
    private string _agentName = "Claude";
    private string _agentPurpose = "";
    private string _bootstrapCode = "";
    private string? _planHash;
    private IReadOnlyList<SetupIssue> _issues = [];

    /// <param name="service">The provisioning client.</param>
    /// <param name="custodyDefaults">The key-storage default for this machine.</param>
    /// <param name="bearerToken">Returns the signed-in owner's access token, or null. Used once the owner exists; before that the bootstrap code is used.</param>
    public CompanySetupViewModel(ICompanySetupService service, IKeyCustodyDefaults custodyDefaults, Func<Task<string?>>? bearerToken = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        ArgumentNullException.ThrowIfNull(custodyDefaults);
        _bearerToken = bearerToken ?? (() => Task.FromResult<string?>(null));
        _timezone = LocalTimezoneName();
        _kekProvider = custodyDefaults.DefaultProvider ?? "";
        _kekReference = custodyDefaults.DefaultReference;
        _check = new ClientCommand(CheckAsync, () => Phase == CompanySetupPhase.Form && IsComplete);
        _apply = new ClientCommand(ApplyAsync, () => Phase == CompanySetupPhase.Review && _planHash is not null);
        _edit = new ClientCommand(() => { Phase = CompanySetupPhase.Form; Message = "Change what you need, then check the plan again."; return Task.CompletedTask; }, () => Phase == CompanySetupPhase.Review);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised once when the company has been created.</summary>
    public event EventHandler? Completed;

    public CompanySetupPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(nameof(CanEditForm));
            RaiseCanExecute();
        }
    }

    /// <summary>One plain sentence saying what happened or what to do next.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string CompanyName
    {
        get => _companyName;
        set
        {
            if (!Set(ref _companyName, value ?? "")) return;
            if (!_slugEdited) SetSlug(SetupPlanText.SlugFrom(_companyName));
            Changed();
        }
    }

    /// <summary>Follows the company name until the person edits it. It cannot change after the company exists.</summary>
    public string Slug
    {
        get => _slug;
        set
        {
            // A view echoing the derived slug back is not an edit.
            if (string.Equals(value ?? "", _slug, StringComparison.Ordinal)) return;
            _slugEdited = true;
            SetSlug(value ?? "");
            Changed();
        }
    }

    public string OwnerName
    {
        get => _ownerName;
        set { if (Set(ref _ownerName, value ?? "")) Changed(); }
    }

    public string Locale
    {
        get => _locale;
        set { if (Set(ref _locale, value ?? "")) Changed(); }
    }

    public string Timezone
    {
        get => _timezone;
        set { if (Set(ref _timezone, value ?? "")) Changed(); }
    }

    /// <summary>Key-encryption-key provider. Pre-filled from this machine's default; empty when the person must choose.</summary>
    public string KekProvider
    {
        get => _kekProvider;
        set { if (Set(ref _kekProvider, value ?? "")) Changed(); }
    }

    public string KekReference
    {
        get => _kekReference;
        set { if (Set(ref _kekReference, value ?? "")) Changed(); }
    }

    /// <summary>The recorded acknowledgement that no backup target is set yet. Kodo keeps flagging the missing target (doc 45 open question 5).</summary>
    public bool AcknowledgeNoBackup
    {
        get => _acknowledgeNoBackup;
        set { if (Set(ref _acknowledgeNoBackup, value)) Changed(); }
    }

    /// <summary>The first agent's name. Claude unless the owner changes it.</summary>
    public string AgentName
    {
        get => _agentName;
        set { if (Set(ref _agentName, value ?? "")) Changed(); }
    }

    /// <summary>What the first agent is for, in the owner's words. May be empty.</summary>
    public string AgentPurpose
    {
        get => _agentPurpose;
        set { if (Set(ref _agentPurpose, value ?? "")) Changed(); }
    }

    /// <summary>The owner's own tick that accepts the Constitution so the default agent may start. Never set for the owner, never defaulted on.</summary>
    public bool AcceptConstitution
    {
        get => _acceptConstitution;
        set { if (Set(ref _acceptConstitution, value)) Changed(); }
    }

    /// <summary>The one-time code the server console printed. Needed until the owner exists. Never stored.</summary>
    public string BootstrapCode
    {
        get => _bootstrapCode;
        set { if (Set(ref _bootstrapCode, value ?? "")) Changed(); }
    }

    /// <summary>True while the form can be edited.</summary>
    public bool CanEditForm => Phase == CompanySetupPhase.Form;

    /// <summary>The hash of the checked plan, shown for review. Null until a check passes.</summary>
    public string? PlanHash
    {
        get => _planHash;
        private set => Set(ref _planHash, value);
    }

    /// <summary>The server's findings from the last check or apply.</summary>
    public IReadOnlyList<SetupIssue> Issues
    {
        get => _issues;
        private set => Set(ref _issues, value);
    }

    public ICommand CheckCommand => _check;

    public ICommand ApplyCommand => _apply;

    public ICommand EditCommand => _edit;

    private bool IsComplete =>
        !string.IsNullOrWhiteSpace(_companyName) && !string.IsNullOrWhiteSpace(_slug) && !string.IsNullOrWhiteSpace(_ownerName)
        && !string.IsNullOrWhiteSpace(_locale) && !string.IsNullOrWhiteSpace(_timezone)
        && !string.IsNullOrWhiteSpace(_agentName) && !string.IsNullOrWhiteSpace(_kekProvider) && !string.IsNullOrWhiteSpace(_kekReference)
        // The form has no backup target field, so the recorded acknowledgement is always needed (doc 45 open question 5).
        && _acknowledgeNoBackup;

    private CompanySetupRequest Request() => new(
        _companyName.Trim(), _slug.Trim(), _ownerName.Trim(), _locale.Trim(), _timezone.Trim(), _kekProvider.Trim(), _kekReference.Trim(), _acknowledgeNoBackup, _acceptConstitution, _agentName.Trim(), _agentPurpose.Trim());

    private async Task<SetupAccess> AccessAsync() => new(string.IsNullOrWhiteSpace(_bootstrapCode) ? null : _bootstrapCode, await _bearerToken());

    private async Task CheckAsync()
    {
        Phase = CompanySetupPhase.Checking;
        Message = "Checking the plan. Nothing is changed yet.";
        var result = await _service.ValidateAsync(Request(), await AccessAsync());
        Issues = result.Issues;
        PlanHash = result.PlanHash;

        if (result.Outcome == SetupCallOutcome.Ok && result.PlanHash is not null)
        {
            Phase = CompanySetupPhase.Review;
            Message = "The plan is valid. Review it, then create the company.";
            return;
        }

        Phase = CompanySetupPhase.Form;
        Message = result.Message;
    }

    private async Task ApplyAsync()
    {
        var hash = _planHash;
        if (hash is null) return;

        Phase = CompanySetupPhase.Applying;
        Message = "Creating your company.";
        var result = await _service.ApplyAsync(Request(), hash, await AccessAsync());
        Issues = result.Issues;

        if (result.Outcome == SetupCallOutcome.Ok)
        {
            Phase = CompanySetupPhase.Done;
            Message = result.Replayed ? "Your company was already created from this plan." : "Your company is created.";
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // A refused or held apply leaves the plan as it was, so the person goes back to the form with the reason.
        PlanHash = null;
        Phase = CompanySetupPhase.Form;
        Message = result.Message;
    }

    private void SetSlug(string value)
    {
        if (Set(ref _slug, value, nameof(Slug))) _check.Changed();
    }

    private void Changed() => _check.Changed();

    private void RaiseCanExecute()
    {
        _check.Changed();
        _apply.Changed();
        _edit.Changed();
    }

    private static string LocalTimezoneName()
    {
        var local = TimeZoneInfo.Local;
        if (!local.HasIanaId && TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana)) return iana;
        return local.Id;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
