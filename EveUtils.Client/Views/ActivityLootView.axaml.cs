using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>
/// The LOOT section grouped by character (ET-215) — drawn once, here, and placed by both the saved activity's detail
/// screen and the run window, so the two show one list the same way rather than two lists that drift apart.
/// </summary>
public partial class ActivityLootView : UserControl
{
    public ActivityLootView() => AvaloniaXamlLoader.Load(this);
}
