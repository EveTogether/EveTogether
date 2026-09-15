using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>The run window's MINING body (ET-229).</summary>
public partial class MiningWindowSectionView : UserControl
{
    public MiningWindowSectionView() => AvaloniaXamlLoader.Load(this);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        MiningLedger.FitTo(this.GetControl<StackPanel>("Ledger"), e.NewSize.Width);
    }
}
