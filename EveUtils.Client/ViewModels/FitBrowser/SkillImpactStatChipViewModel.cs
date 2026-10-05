using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One "OPTIMISE FOR" chip (ET-356 D3): a toggle for one <see cref="SkillImpactStat"/>, grey with a reason when the
/// scan found nothing that could move it for this fit — either no skill touches it at all, every skill that does is
/// already trained to V, or (Optimal/Falloff/Tracking) the fit carries no turret or drone weapon to read it from.
/// </summary>
public sealed class SkillImpactStatChipViewModel : ViewModelBase
{
    // Unselected by default; after the scan the owner selects the fit's own kind (weapons → DPS) when nothing is
    // chosen yet, so the screen never opens empty.
    private bool _isSelected;
    private bool _isMatch = true;
    private bool _isAvailable = true;
    private string? _unavailableReason;

    public SkillImpactStatChipViewModel(SkillImpactStats.Meta meta)
    {
        Stat = meta.Stat;
        Group = meta.Group;
        Label = meta.Label;
        MenuLabel = meta.MenuLabel;
        RuleName = meta.RuleName;
        RemoveCommand = new RelayCommand(() => IsSelected = false);
    }

    public SkillImpactStat Stat { get; }
    public string Group { get; }
    public string Label { get; }
    public string MenuLabel { get; }
    public string RuleName { get; }

    /// <summary>The × on an OPTIMISE FOR chip.</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>False while the stat menu's filter text matches neither label.</summary>
    public bool IsMatch
    {
        get => _isMatch;
        set => SetProperty(ref _isMatch, value);
    }

    /// <summary>Raised after a toggle actually changes the selection, so the owner recomputes the impact list without
    /// rescanning — the scan's result is cached and only the ranking/filter is redone.</summary>
    public event Action? SelectionChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke();
            }
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

/// <summary>One fit-detail section in the "+ stat" menu (OFFENSE, TANK, …), hidden while the filter matches none of it.</summary>
public sealed class SkillImpactStatGroupViewModel(string name, IReadOnlyList<SkillImpactStatChipViewModel> chips) : ViewModelBase
{
    public string Name { get; } = name;
    public IReadOnlyList<SkillImpactStatChipViewModel> Chips { get; } = chips;
    public bool IsVisible => Chips.Any(chip => chip.IsMatch);

    public void Refresh() => OnPropertyChanged(nameof(IsVisible));
}
