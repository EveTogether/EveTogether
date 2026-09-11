using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The detail screen's BOUNTY body (ET-236).</summary>
public partial class BountyDetailSectionView : UserControl
{
    public BountyDetailSectionView() => AvaloniaXamlLoader.Load(this);
}
