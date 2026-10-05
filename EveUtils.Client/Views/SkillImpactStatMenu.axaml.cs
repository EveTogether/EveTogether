using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>SKILL IMPACT's "+ stat ▾" menu content, hosted in the OPTIMISE FOR flyout.</summary>
public partial class SkillImpactStatMenu : UserControl
{
    public SkillImpactStatMenu() => AvaloniaXamlLoader.Load(this);
}
