using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's LOOT body (ET-236).</summary>
public partial class LootWindowSectionView : UserControl
{
    public LootWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
