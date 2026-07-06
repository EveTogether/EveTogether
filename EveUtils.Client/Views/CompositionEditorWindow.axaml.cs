using System;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// The create/edit composition view. The view-model persists on save (diff-and-replay of the granular
/// commands) and raises <see cref="CompositionEditorViewModel.CloseRequested"/>. Hosted like the other feature
/// modules (<see cref="IHostableModuleWindow"/>): a docked tab when docked, a floating window when floating — so
/// closing routes through the host (dismiss the tab) or closes the real window. The library is told whether to
/// reload by the dialog service, which observes the same close event (saved vs cancel).
/// </summary>
public partial class CompositionEditorWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    public CompositionEditorWindow() => AvaloniaXamlLoader.Load(this);

    public CompositionEditorWindow(CompositionEditorViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += _ =>
        {
            if (CloseRequested is not null) CloseRequested();   // docked → dismiss the hosted tab
            else Close();                                       // floating → close the real window
        };
    }
}
