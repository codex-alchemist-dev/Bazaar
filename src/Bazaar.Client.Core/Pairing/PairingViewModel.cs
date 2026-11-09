using System.ComponentModel;
using System.Runtime.CompilerServices;
using Bazaar.Client.Core.Server;

namespace Bazaar.Client.Core.Pairing;

/// <summary>Where a pairing attempt stands. <see cref="Failed"/> keeps the form usable so the user can try again.</summary>
public enum PairingStatus
{
    Idle,
    Pairing,
    Paired,
    Failed,
}

/// <summary>Raised when pairing succeeds: the server the device paired with and the grant it received.</summary>
/// <param name="baseAddress">The server the device paired with.</param>
/// <param name="grant">The credential pair. Only the refresh token has been saved by <see cref="PairingFlow"/>.</param>
public sealed class PairingCompletedEventArgs(Uri baseAddress, PairingGrant grant) : EventArgs
{
    /// <summary>The server the device paired with.</summary>
    public Uri BaseAddress { get; } = baseAddress;

    /// <summary>The credential pair the server issued.</summary>
    public PairingGrant Grant { get; } = grant;
}

/// <summary>
/// The state of the pairing form: the server address, the code the user typed, and the outcome of the last attempt. Holds no
/// Avalonia types, so it can be tested without a window. Reports changes through <see cref="INotifyPropertyChanged"/>.
/// </summary>
public sealed class PairingViewModel(PairingFlow flow, string deviceClass) : INotifyPropertyChanged
{
    /// <summary>The label the approver sees for this device.</summary>
    public const string DefaultLabel = "This device";

    /// <summary>The client build the server sees on a cold-start refresh.</summary>
    public const string ClientBuild = "bazaar-android";

    private readonly PairingFlow _flow = flow ?? throw new ArgumentNullException(nameof(flow));
    private readonly string _deviceClass = !string.IsNullOrWhiteSpace(deviceClass)
        ? deviceClass
        : throw new ArgumentException("A device class is required.", nameof(deviceClass));

    private string _serverAddress = ServerEndpoint.Development.BaseAddress.ToString();
    private string _userCode = string.Empty;
    private string _label = DefaultLabel;
    private PairingStatus _status = PairingStatus.Idle;
    private string _message = "Enter the code shown on your other device.";
    private CancellationTokenSource? _inFlight;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised once when pairing succeeds.</summary>
    public event EventHandler<PairingCompletedEventArgs>? Paired;

    /// <summary>The server to pair with, as typed by the user. Defaults to the development endpoint.</summary>
    public string ServerAddress
    {
        get => _serverAddress;
        set
        {
            if (SetField(ref _serverAddress, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanPair));
            }
        }
    }

    /// <summary>The code shown on the other device. Spaces and dashes are removed when it is sent.</summary>
    public string UserCode
    {
        get => _userCode;
        set
        {
            if (SetField(ref _userCode, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanPair));
            }
        }
    }

    /// <summary>The label the approver sees for this device.</summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value ?? string.Empty);
    }

    /// <summary>Where the current attempt stands.</summary>
    public PairingStatus Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(CanPair));
            }
        }
    }

    /// <summary>Human text for the current status. Shows the server's error when pairing fails.</summary>
    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    /// <summary>True when a pairing attempt may start: not already pairing or paired, a code is typed, and the address is an absolute http or https URI.</summary>
    public bool CanPair =>
        (Status is PairingStatus.Idle or PairingStatus.Failed)
        && !string.IsNullOrWhiteSpace(UserCode)
        && TryParseServerAddress(ServerAddress, out _);

    /// <summary>
    /// Starts a pairing attempt. Does nothing when <see cref="CanPair"/> is false or an attempt is already running. Errors are shown
    /// through <see cref="Status"/> and <see cref="Message"/>; this method does not throw for them.
    /// </summary>
    public async Task PairAsync(CancellationToken cancellationToken = default)
    {
        if (!CanPair)
        {
            return;
        }

        var address = new Uri(ServerAddress.Trim(), UriKind.Absolute);
        _inFlight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Status = PairingStatus.Pairing;
        Message = "Waiting for approval on your other device.";

        try
        {
            var grant = await _flow.PairAsync(address, UserCode, Label, _deviceClass, _inFlight.Token).ConfigureAwait(true);
            Status = PairingStatus.Paired;
            Message = "Paired.";
            Paired?.Invoke(this, new PairingCompletedEventArgs(address, grant));
        }
        catch (PairingException exception)
        {
            Fail(MessageFor(exception.Code));
        }
        catch (OperationCanceledException)
        {
            Fail("Pairing was cancelled.");
        }
        catch (HttpRequestException)
        {
            Fail("Could not reach the server.");
        }
        finally
        {
            _inFlight.Dispose();
            _inFlight = null;
        }
    }

    /// <summary>
    /// Cold start: tries to restore a pairing from the stored refresh credential. Returns <c>true</c> and raises <see cref="Paired"/>
    /// when the device is restored. Returns <c>false</c> when it is not paired, the server refuses the credential, the server
    /// address is not usable, or the server cannot be reached. It does not throw. A transport failure sets <see cref="Message"/>
    /// to "Could not reach the server."; other failures leave the form idle.
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!TryParseServerAddress(ServerAddress, out var address))
        {
            return false;
        }

        try
        {
            var grant = await _flow.RestoreAsync(address, _deviceClass, ClientBuild, cancellationToken).ConfigureAwait(true);
            if (grant is null)
            {
                return false;
            }

            Status = PairingStatus.Paired;
            Message = "Paired.";
            Paired?.Invoke(this, new PairingCompletedEventArgs(address, grant));
            return true;
        }
        catch (HttpRequestException)
        {
            Status = PairingStatus.Idle;
            Message = "Could not reach the server.";
            return false;
        }
        catch (PairingException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Cancels the attempt in flight, if any. The attempt ends with <see cref="PairingStatus.Failed"/>.</summary>
    public void Cancel() => _inFlight?.Cancel();

    private void Fail(string message)
    {
        Status = PairingStatus.Failed;
        Message = message;
    }

    private static string MessageFor(string code) => code switch
    {
        "expired_token" => "That code expired. Ask for a new one.",
        "access_denied" => "The request was denied on your other device.",
        _ => "Pairing failed (" + code + ").",
    };

    private static bool TryParseServerAddress(string value, out Uri address)
    {
        address = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
