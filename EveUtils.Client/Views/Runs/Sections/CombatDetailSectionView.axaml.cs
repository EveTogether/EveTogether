using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The detail screen's COMBAT body (ET-468).</summary>
public partial class CombatDetailSectionView : UserControl
{
    public CombatDetailSectionView() => AvaloniaXamlLoader.Load(this);
}
