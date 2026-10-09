using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The detail screen's TIMELINE body (ET-468).</summary>
public partial class TimelineDetailSectionView : UserControl
{
    public TimelineDetailSectionView() => AvaloniaXamlLoader.Load(this);
}
