using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Skills.WhatIf;

namespace EveUtils.Client.Views.Skills;

/// <summary>The SHARE dialog for a skill plan (ET-358): closed by its own view model on Close, or once a copy or a
/// hand-off to the doctrine editor completes.</summary>
public partial class SkillPlanShareDialogWindow : ChromedWindow
{
    public SkillPlanShareDialogWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SkillPlanShareDialogWindow(SkillPlanShareDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
    }
}
