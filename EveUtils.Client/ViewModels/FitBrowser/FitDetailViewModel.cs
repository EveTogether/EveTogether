using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Inline preview of one selected fit in the browser: a slot-list grouped by ESI <c>flag</c> in EVE order.
/// The full radial view with computed stats lives in <see cref="FitDetailWindowViewModel"/> (double-click a row).
/// </summary>
public sealed class FitDetailViewModel
{
    public string Name { get; }
    public int ShipTypeId { get; }

    /// <summary>Hull name from the SDE (or <c>type {id}</c> until it is imported).</summary>
    public string ShipTypeLabel { get; }

    public IReadOnlyList<FitSlotGroupViewModel> SlotGroups { get; }

    public FitDetailViewModel(EsiFitting fit, ISdeNameResolver names)
    {
        Name = fit.Name;
        ShipTypeId = fit.ShipTypeId;
        ShipTypeLabel = names.TypeName(fit.ShipTypeId);

        SlotGroups = fit.Items
            .GroupBy(item => FitSlotClassifier.Classify(item.Flag))
            .OrderBy(group => (int)group.Key)
            .Select(group => new FitSlotGroupViewModel(
                group.Key,
                group.OrderBy(item => FitSlotClassifier.SlotIndex(item.Flag))
                    .Select(item => new FitSlotItemViewModel(item.Flag, item.TypeId, item.Quantity, names.TypeName(item.TypeId)))
                    .ToList()))
            .ToList();
    }
}
