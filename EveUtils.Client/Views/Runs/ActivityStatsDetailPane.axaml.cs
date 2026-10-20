using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs;

/// <summary>One activity's earnings read out beside the BY ACTIVITY list, or inside the drawer over it (ET-517).</summary>
public partial class ActivityStatsDetailPane : UserControl
{
    public ActivityStatsDetailPane() => AvaloniaXamlLoader.Load(this);
}
