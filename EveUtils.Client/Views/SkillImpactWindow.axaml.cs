using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.FitBrowser;

namespace EveUtils.Client.Views;

/// <summary>
/// SKILL IMPACT… (ET-356), hosted like FIT DETAIL: a docked tab when docked, its own window when floating.
/// </summary>
public partial class SkillImpactWindow : ChromedWindow
{
    public SkillImpactWindow() => AvaloniaXamlLoader.Load(this);

    public SkillImpactWindow(SkillImpactViewModel viewModel) : this() => DataContext = viewModel;
}
