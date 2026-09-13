using Avalonia.Controls;

namespace EveUtils.Client.Views;

/// <summary>
/// One member's combat figures with a meter each (ET-277, design D). A control rather than a template, so the fleet
/// screen's list and grid and the DPS pop-out show exactly the same rows.
/// </summary>
public partial class CombatMeters : UserControl
{
    public CombatMeters() => InitializeComponent();
}
