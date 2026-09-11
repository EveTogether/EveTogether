using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's MISSION body (ET-237).</summary>
public partial class MissionWindowSectionView : UserControl
{
    public MissionWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
