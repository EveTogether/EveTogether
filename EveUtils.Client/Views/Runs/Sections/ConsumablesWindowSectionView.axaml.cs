using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's CONSUMABLES body (ET-249).</summary>
public partial class ConsumablesWindowSectionView : UserControl
{
    public ConsumablesWindowSectionView() => AvaloniaXamlLoader.Load(this);
}
