using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Setup;

namespace EveUtils.Client.Views;

/// <summary>The setup wizard (ET-425). Everything it shows and does lives in <see cref="SetupWizardViewModel"/>.</summary>
public partial class SetupWizardWindow : ChromedWindow
{
    public SetupWizardWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SetupWizardWindow(SetupWizardViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Closed += (_, _) => viewModel.CloseRequested -= Close;
    }
}
