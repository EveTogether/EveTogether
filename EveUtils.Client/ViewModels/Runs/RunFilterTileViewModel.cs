using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One tile in the TYPES or CHARACTERS column (ET-293): a type's icon or a character's own <c>HexPortrait</c>, its
/// full name — never truncated, an EVE character's can run to 37 characters — and how many of the tab's activities
/// it covers. On by default; a click hides it, alt-click solos it, Space togglet the focused one.
///
/// <see cref="Key"/> is boxed on purpose: a <see cref="EveUtils.Shared.Modules.Runs.Enums.RunTypeId"/> for a TYPES
/// tile, a character id (<see langword="long"/>) for a CHARACTERS one — the one thing <see cref="RunsOverviewViewModel"/>
/// needs back to know which tile fired, and what a live refresh reconciles a tile by, so a focused or hovered tile
/// survives one (ET-287, ET-222).
/// </summary>
public sealed partial class RunFilterTileViewModel : ObservableObject
{
    private readonly Action<object> _toggle;
    private readonly Action<object> _solo;

    public RunFilterTileViewModel(object key, string name, MaterialIconKind? icon, CharacterFaceViewModel? face,
        Action<object> toggle, Action<object> solo)
    {
        Key = key;
        Name = name;
        Icon = icon;
        Face = face;
        _toggle = toggle;
        _solo = solo;
    }

    public object Key { get; }

    public string Name { get; }

    /// <summary>Set for a TYPES tile, null for a CHARACTERS one — the template draws whichever of this and
    /// <see cref="Face"/> is there.</summary>
    public MaterialIconKind? Icon { get; }

    /// <summary>Set for a CHARACTERS tile: the same shared face <see cref="RunsOverviewViewModel"/> already loads a
    /// portrait for elsewhere on this screen, so this tile never asks for one of its own.</summary>
    public CharacterFaceViewModel? Face { get; }

    [ObservableProperty] private int _count;

    [ObservableProperty] private bool _isOn = true;

    /// <summary>A click, or Space on the focused tile: hides this tile's activities, or brings them back.</summary>
    public void Toggle() => _toggle(Key);

    /// <summary>Alt-click: this tile alone stays on, every other tile in the block goes off.</summary>
    public void Solo() => _solo(Key);
}
