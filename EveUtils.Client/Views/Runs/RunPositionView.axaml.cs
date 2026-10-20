using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs;

/// <summary>Where a saved run stands among the runs of its activity (ET-520), in the run details and in the runs drawer.</summary>
public partial class RunPositionView : UserControl
{
    public RunPositionView() => AvaloniaXamlLoader.Load(this);
}
