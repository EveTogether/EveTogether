using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace EveUtils.Client.Views;

/// <summary>The compact run window as one line (ET-478). Hosted by <see cref="ActivityWindow"/>, bound to the same
/// view model as the full view.</summary>
public partial class ActivityCompactHudView : UserControl
{
    public ActivityCompactHudView() => InitializeComponent();

    // The hover content is not in this view's tree until it opens, so it is handed the view model by hand.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        LootTip.DataContext = DataContext;
    }

    private void OnLinePressed(object? sender, PointerPressedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as ActivityWindow)?.BeginDrag(e);
}
