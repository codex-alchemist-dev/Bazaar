using Avalonia.Controls;

namespace Bazaar.Portability.Client.Avalonia;

/// <summary>The first step: pick the source file. Its command comes from <see cref="WizardViewModel"/>.</summary>
public sealed partial class ChooseSourceStepView : UserControl
{
    public ChooseSourceStepView() => InitializeComponent();
}
