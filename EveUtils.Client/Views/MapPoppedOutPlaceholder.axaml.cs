using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>The MAP tab's stand-in while the map is popped out (ET-396); its buttons are the map view model's own commands.</summary>
public partial class MapPoppedOutPlaceholder : UserControl
{
    public MapPoppedOutPlaceholder() => AvaloniaXamlLoader.Load(this);
}
