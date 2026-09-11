using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The detail screen's LOOT body (ET-236).</summary>
public partial class LootDetailSectionView : UserControl
{
    public LootDetailSectionView() => AvaloniaXamlLoader.Load(this);
}
