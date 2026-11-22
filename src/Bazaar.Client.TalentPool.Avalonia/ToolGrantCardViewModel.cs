using System.ComponentModel;
using Bazaar.Client.Approvals;
using Bazaar.Client.Approvals.Avalonia;

namespace Bazaar.Client.TalentPool.Avalonia;

/// <summary>
/// The owner's Talent tool-grant card (docs/design/25). It shows the template and the exact tools. Approve goes through
/// <see cref="ToolGrantGate"/>, which asks for the biometric. There is no deny button: declining a grant is doing nothing, and
/// a hire vote never implies it. Dictation fills no field and decides nothing.
/// </summary>
public sealed class ToolGrantCardViewModel : INotifyPropertyChanged
{
    private readonly ToolGrantGate _gate;
    private string _notice = "";
    private bool _isBusy;
    private bool _isDone;

    public ToolGrantCardViewModel(ToolGrantGate gate, ToolGrantCard card)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        Card = card ?? throw new ArgumentNullException(nameof(card));
        ApproveCommand = new ApprovalCommand(() => _ = ApproveAsync(), () => CanApprove);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolGrantCard Card { get; }

    public ApprovalCommand ApproveCommand { get; }

    public string Title => $"Grant tools to {Card.Name} (version {Card.Version})";

    public IReadOnlyList<string> Tools => Card.Tools;

    public string Notice => _notice;

    public bool HasNotice => _notice.Length > 0;

    public bool CanApprove => !_isBusy && !_isDone && Card.Tools.Count > 0;

    /// <summary>Runs the approval through the gate and shows the outcome.</summary>
    public async Task ApproveAsync()
    {
        if (!CanApprove)
        {
            return;
        }

        _isBusy = true;
        Raise();
        try
        {
            var result = await _gate.ApproveAsync(Card, ApprovalInputSource.CardButton, CancellationToken.None);
            _isDone = result.IsSent || result.Refusal is ToolGrantRefusal.SendFailed or ToolGrantRefusal.AlreadyDecided;
            _notice = result.Refusal switch
            {
                null => "Tool grant approved.",
                ToolGrantRefusal.BiometricCancelled => "Cancelled. Nothing was sent.",
                ToolGrantRefusal.BiometricFailed => "Fingerprint or face check failed. Nothing was sent.",
                ToolGrantRefusal.SigningFailed => "The approval key could not sign. Nothing was sent.",
                ToolGrantRefusal.AlreadyDecided => "This grant was already signed on this device.",
                ToolGrantRefusal.SendFailed => "The grant was signed but could not be sent. It will not be resent; check the template.",
                _ => "This grant was refused. Nothing was sent.",
            };
        }
        finally
        {
            _isBusy = false;
            Raise();
        }
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        ApproveCommand.RaiseCanExecuteChanged();
    }
}
