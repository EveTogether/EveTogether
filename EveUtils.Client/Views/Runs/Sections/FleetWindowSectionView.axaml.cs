using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's FLEET body (ET-236).</summary>
public partial class FleetWindowSectionView : UserControl
{
    public FleetWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
