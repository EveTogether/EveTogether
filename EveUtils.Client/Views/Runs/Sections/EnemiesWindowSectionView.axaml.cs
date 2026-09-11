using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's ENEMIES body (ET-236).</summary>
public partial class EnemiesWindowSectionView : UserControl
{
    public EnemiesWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
