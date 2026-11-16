using Avalonia.Controls;

namespace Bazaar.Portability.Client.Avalonia;

/// <summary>Hosts the wizard. It shows the step view that matches <see cref="WizardViewModel"/>'s step flags.</summary>
public sealed partial class ImportWizardView : UserControl
{
    public ImportWizardView() => InitializeComponent();
}
