using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Core.Pairing;

/// <summary>
/// The pairing form: a server address, the code from the other device, a Pair button and the status text. Built in code and
/// bound to a <see cref="PairingViewModel"/>, so the button and the message follow the view model as it changes.
/// </summary>
public sealed class PairingView : UserControl
{
    private readonly PairingViewModel _viewModel;

    public PairingView(PairingViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        var serverAddress = new TextBox { PlaceholderText = "Server address" };
        serverAddress.Bind(TextBox.TextProperty, new Binding(nameof(PairingViewModel.ServerAddress)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        var userCode = new TextBox { PlaceholderText = "Code from your other device" };
        userCode.Bind(TextBox.TextProperty, new Binding(nameof(PairingViewModel.UserCode)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        var pair = new Button { Content = "Pair", HorizontalAlignment = HorizontalAlignment.Left };
        pair.Bind(Button.IsEnabledProperty, new Binding(nameof(PairingViewModel.CanPair)));
        pair.Click += (_, _) => _ = _viewModel.PairAsync();

        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        message.Bind(TextBlock.TextProperty, new Binding(nameof(PairingViewModel.Message)));

        Content = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 480,
            Children =
            {
                new TextBlock { Text = "Pair this device", FontWeight = FontWeight.SemiBold, FontSize = 16 },
                serverAddress,
                userCode,
                pair,
                message,
            },
        };
    }

    /// <summary>The view model this form is bound to.</summary>
    public PairingViewModel ViewModel => _viewModel;
}
