using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Skills;

/// <summary>The WHAT IF panel (ET-358), hosted inline in <see cref="SkillsPlansPane"/> for the selected plan.</summary>
public partial class SkillsWhatIfPane : UserControl
{
    public SkillsWhatIfPane()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
