using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>The escalation dialog (ET-125): closed by its own view model on Register or Cancel.</summary>
public partial class EscalationDialogWindow : ChromedWindow
{
    public EscalationDialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public EscalationDialogWindow(EscalationDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += _ => Close();
    }
}
