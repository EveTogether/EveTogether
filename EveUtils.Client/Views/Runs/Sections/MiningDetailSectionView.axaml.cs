using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The detail screen's MINING body (ET-229).</summary>
public partial class MiningDetailSectionView : UserControl
{
    public MiningDetailSectionView() => AvaloniaXamlLoader.Load(this);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        MiningLedger.FitTo(this.GetControl<StackPanel>("Ledger"), e.NewSize.Width);
    }
}
