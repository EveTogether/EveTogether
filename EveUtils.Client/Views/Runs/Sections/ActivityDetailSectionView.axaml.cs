using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The detail screen's ACTIVITY body (ET-236).</summary>
public partial class ActivityDetailSectionView : UserControl
{
    public ActivityDetailSectionView() => AvaloniaXamlLoader.Load(this);
}
