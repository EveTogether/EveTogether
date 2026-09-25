using System.Globalization;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One OPTIMISE-tab implant row: an attribute and its current attribute-implant bonus.</summary>
public sealed record ImplantAttributeRowViewModel(string AttributeName, double CurrentBonus)
{
    public string BonusText => CurrentBonus > 0
        ? $"+{CurrentBonus.ToString("0", CultureInfo.InvariantCulture)}"
        : "none";
}
