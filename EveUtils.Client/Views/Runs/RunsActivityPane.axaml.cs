using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs;

/// <summary>The selected run read out beside the list, or inside the drawer over it (ET-291).</summary>
public partial class RunsActivityPane : UserControl
{
    public RunsActivityPane() => AvaloniaXamlLoader.Load(this);
}
