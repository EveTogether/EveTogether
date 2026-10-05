using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Skills;

/// <summary>From a fit (ET-356/ET-357): the SKILL IMPACT screen as a view inside SKILLS → PLANS, not a window of its own.</summary>
public partial class SkillImpactView : UserControl
{
    public SkillImpactView() => AvaloniaXamlLoader.Load(this);
}
