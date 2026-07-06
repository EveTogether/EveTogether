using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One stance of a Tactical Destroyer in the mode selector: its stance, the command that selects it and
/// whether it is the active mode. The label is the stance name (Defense / Sharpshooter / Propulsion), shown as the hover
/// tooltip. The glyph is drawn as a vector in the view (matched to the in-game selector, ref 09) — an upward chevron
/// shared by all stances, framed per <see cref="Stance"/> — because the mode subsystems have no distinct type-image of
/// their own (every mode type id resolves to the same generic placeholder render on the image server).</summary>
public sealed partial class TacticalModeViewModel : ViewModelBase
{
    public int TypeId { get; }
    public string Label { get; }
    public ICommand Select { get; }
    public TacticalStance Stance { get; }

    /// <summary>Defense and Sharpshooter sit inside a ring; Propulsion is a bare chevron (in-game).</summary>
    public bool ShowRing => Stance is TacticalStance.Defense or TacticalStance.Sharpshooter;

    /// <summary>Sharpshooter adds crosshair ticks around the ring (the in-game targeting reticle).</summary>
    public bool ShowCrosshair => Stance is TacticalStance.Sharpshooter;

    [ObservableProperty]
    private bool _isSelected;

    public TacticalModeViewModel(int typeId, string label, ICommand select)
    {
        TypeId = typeId;
        Label = label;
        Select = select;
        Stance = _StanceFromLabel(label);
    }

    private static TacticalStance _StanceFromLabel(string label) =>
        label switch
        {
            _ when label.Contains("Defense", StringComparison.OrdinalIgnoreCase) => TacticalStance.Defense,
            _ when label.Contains("Sharpshooter", StringComparison.OrdinalIgnoreCase) => TacticalStance.Sharpshooter,
            _ when label.Contains("Propulsion", StringComparison.OrdinalIgnoreCase) => TacticalStance.Propulsion,
            _ => TacticalStance.Unknown
        };
}
