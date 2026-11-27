using System.ComponentModel;
using Bazaar.Client.Approvals.Avalonia;

namespace Bazaar.Client.OwnerId.Avalonia;

/// <summary>
/// The pairing card that stores the owner's human id on this phone (doc 32). It stores text the person typed or pasted and nothing
/// else: no key is created, and the approval key is enrolled separately (doc 32 Question 4). Saving does not start approving
/// anything; it only lets the gates be built the next time the app opens.
/// </summary>
public sealed class OwnerIdPairingViewModel : INotifyPropertyChanged
{
    private readonly IOwnerIdStore _store;
    private string _input = "";
    private string _notice = "";

    public OwnerIdPairingViewModel(IOwnerIdStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        StoredId = store.Load();
        SaveCommand = new ApprovalCommand(Save, () => OwnerIdText.TryNormalize(_input, out _));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ApprovalCommand SaveCommand { get; }

    /// <summary>The id already stored, or null.</summary>
    public string? StoredId { get; private set; }

    public bool IsPaired => StoredId is not null;

    public string Status => StoredId is null ? "No owner id is stored on this phone." : $"Owner id stored: {StoredId}";

    public string Input
    {
        get => _input;
        set
        {
            if (_input == value)
            {
                return;
            }

            _input = value ?? "";
            Raise(nameof(Input));
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public string Notice => _notice;

    public bool HasNotice => _notice.Length > 0;

    /// <summary>Validates and stores the typed id.</summary>
    public void Save()
    {
        if (!OwnerIdText.TryNormalize(_input, out var canonical))
        {
            SetNotice("That is not a valid owner id. Nothing was stored.");
            return;
        }

        if (!_store.Save(canonical))
        {
            SetNotice("The owner id could not be stored on this phone.");
            return;
        }

        StoredId = canonical;
        _input = "";
        SetNotice("Owner id stored. Reopen the app to start approving. No key was created; your approval key is set up separately.");
        Raise(string.Empty);
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void SetNotice(string text)
    {
        _notice = text;
        Raise(nameof(Notice));
        Raise(nameof(HasNotice));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
