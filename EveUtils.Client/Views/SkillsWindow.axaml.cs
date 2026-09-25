using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Skills;

namespace EveUtils.Client.Views;

/// <summary>The SKILLS module window (ET-16). A <see cref="ChromedWindow"/> like the other feature modules, so the
/// shell hosts it docked or floating.</summary>
public partial class SkillsWindow : ChromedWindow
{
    public SkillsWindow() => AvaloniaXamlLoader.Load(this);

    public SkillsWindow(SkillsWindowViewModel viewModel) : this() => DataContext = viewModel;
}
