namespace EveUtils.Client.ViewModels;

/// <summary>How the reusable fit picker behaves.</summary>
public enum FitPickerMode
{
    /// <summary>Checkboxes + ADD — add several fits at once (the composition editor "+ ADD FIT").</summary>
    Multi,

    /// <summary>A Select button per row that picks one fit immediately (the fleet-manager member assign).</summary>
    Single
}
