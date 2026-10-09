using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs;

/// <summary>The AUTO badge and certainty dots of a detected room (ET-368).</summary>
public partial class RoomSourceView : UserControl
{
    public RoomSourceView() => AvaloniaXamlLoader.Load(this);
}
