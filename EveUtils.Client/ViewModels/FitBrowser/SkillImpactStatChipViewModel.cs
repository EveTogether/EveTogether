using System;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One "OPTIMISE FOR" chip (ET-356 D3): a toggle for one <see cref="SkillImpactStat"/>, grey with a reason when the
/// scan found nothing that could move it for this fit — either no skill touches it at all, every skill that does is
/// already trained to V, or (Optimal/Falloff/Tracking) the fit carries no turret or drone weapon to read it from.
/// </summary>
public sealed class SkillImpactStatChipViewModel : ViewModelBase
{
    // Unselected by default: "OPTIMISE FOR" starts empty so the impact list only ranks what the pilot actually
    // asked about, rather than an unfiltered dump of every stat the scan happened to find a mover for.
    private bool _isSelected;
    private bool _isAvailable = true;
    private string? _unavailableReason;

    public SkillImpactStatChipViewModel(SkillImpactStat stat, string group, string label)
    {
        Stat = stat;
        Group = group;
        Label = label;
    }

    public SkillImpactStat Stat { get; }
    public string Group { get; }
    public string Label { get; }

    /// <summary>Raised after a toggle actually changes the selection, so the owner recomputes the impact list without
    /// rescanning — the scan's result is cached and only the ranking/filter is redone.</summary>
    public event Action? SelectionChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke();
        }
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public string? UnavailableReason
    {
        get => _unavailableReason;
        private set => SetProperty(ref _unavailableReason, value);
    }

    public void SetAvailable()
    {
        IsAvailable = true;
        UnavailableReason = null;
    }

    public void SetUnavailable(string reason)
    {
        IsAvailable = false;
        UnavailableReason = reason;
        IsSelected = false;
    }
}
