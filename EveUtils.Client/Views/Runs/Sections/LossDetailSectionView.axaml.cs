using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The detail screen's LINKED LOSS body (ET-331).</summary>
public partial class LossDetailSectionView : UserControl
{
    public LossDetailSectionView() => AvaloniaXamlLoader.Load(this);
}
