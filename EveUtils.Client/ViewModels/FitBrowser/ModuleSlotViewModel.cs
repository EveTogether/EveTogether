using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One interactive module box on the radial fitting wheel: its canvas position, the module name + slot
/// category, its activation state and its loaded charge. Left-click cycles the states the module supports
/// (offline → online → active → overloaded); right-click opens a menu of the charges it accepts (plus remove). Either
/// change asks the owner to recompute the fit's stats and reloads the box icon. The box colour reflects the state,
/// mirroring the in-game fitting sim — green active, red overloaded, dim offline, category colour online.
/// </summary>
public sealed partial class ModuleSlotViewModel : ViewModelBase
{
    private readonly ModuleState[] _validStates;
    private readonly ITypeImageProvider? _images;
    private readonly Func<Task> _onChanged;

    public int TypeId { get; }
    public FitSlotCategory Category { get; }
    public string Name { get; }
    /// <summary>The slot's category + ordinal ("HIGH 1", "MID 2", …) for the tooltip, standing in for the in-game slot
    /// hotkey; empty when no slot number was supplied.</summary>
    public string SlotLabel { get; }
    /// <summary>The curved annular-segment tile (its top edge follows the ring's outer radius, its bottom the inner one)
    /// so the slots tile the ring edge-to-edge like the in-game / eveship wheel, instead of
    /// upright or rotated rectangles. Parsed lazily so constructing the view-model needs no render backend (tests).</summary>
    private readonly string _shapePath;
    private Geometry? _shape;
    public Geometry Shape => _shape ??= Geometry.Parse(_shapePath);
    /// <summary>Top-left of the upright module icon, at the segment's mid-radius centre (the icon stays upright; only the
    /// tile follows the ring's curve, mirroring in-game).</summary>
    public double IconLeft { get; }
    public double IconTop { get; }
    public string Glyph { get; }

    private readonly IReadOnlyList<SdeChargeType> _chargeOptions;
    private readonly Func<int, Task>? _onShowInfo;
    // Owner-supplied guard: given this slot and the state it would move to, returns false when the activation is refused
    // (e.g. a cloak / active-module conflict) and surfaces the reason. Null = no cross-module rules (headless / no engine).
    private readonly Func<ModuleSlotViewModel, ModuleState, bool>? _approveActivation;

    // This slot's own resolved contribution, set after each recompute; null falls the tooltip back to name + state.
    private ModuleContribution? _contribution;

    /// <summary>The module's right-click menu: Information, Charge Information + Remove when a charge is loaded,
    /// then the charges it accepts. Rebuilt when the charge changes so Remove/Charge-Information come and go with it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChargeMenu))]
    private IReadOnlyList<ChargeMenuOptionViewModel> _chargeMenu = [];

    public bool HasChargeMenu => ChargeMenu.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Color))]
    [NotifyPropertyChangedFor(nameof(FillBrush))]
    [NotifyPropertyChangedFor(nameof(TileOpacity))]
    [NotifyPropertyChangedFor(nameof(TooltipModel))]
    private ModuleState _state;

    [ObservableProperty]
    private int? _chargeTypeId;

    // The CCP type image (charge icon when a charge is loaded, else the module icon — like the in-game ring);
    // null until loaded or when images are disabled, so the box falls back to its glyph.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    public bool HasImage => Image is not null;

    public ModuleSlotViewModel(int typeId, int? chargeTypeId, FitSlotCategory category, string name,
        string shape, double iconLeft, double iconTop, string glyph, ModuleState state, ModuleState[] validStates,
        IReadOnlyList<SdeChargeType> chargeOptions, ITypeImageProvider? images, Func<Task> onChanged,
        Func<int, Task>? onShowInfo = null, Func<ModuleSlotViewModel, ModuleState, bool>? approveActivation = null,
        int slotNumber = 0)
    {
        TypeId = typeId;
        _chargeTypeId = chargeTypeId;
        Category = category;
        Name = name;
        SlotLabel = slotNumber > 0 ? $"{_SlotAbbrev(category)} {slotNumber}" : "";
        _shapePath = shape;
        IconLeft = iconLeft;
        IconTop = iconTop;
        Glyph = glyph;
        _state = state;
        _validStates = validStates.Length > 0 ? validStates : [ModuleState.Online];
        _images = images;
        _onChanged = onChanged;
        _chargeOptions = chargeOptions;
        _onShowInfo = onShowInfo;
        _approveActivation = approveActivation;
        ChargeMenu = BuildMenu();
    }

    public ModuleInput ToInput() => new(TypeId, State, ChargeTypeId);

    /// <summary>The charges this module accepts, for the per-module charge picker in the Charges panel.</summary>
    public IReadOnlyList<SdeChargeType> ChargeOptions => _chargeOptions;

    /// <summary>True when the module can hold a charge, so the Charges panel groups it into a filter icon.</summary>
    public bool CanLoadCharge => _chargeOptions.Count > 0;

    /// <summary>Loads the box image: the charge icon when a charge is loaded, otherwise the module icon.</summary>
    public async Task LoadImageAsync() =>
        Image = _images is null ? null : await _images.GetImageAsync(ChargeTypeId ?? TypeId, TypeImageKind.Icon, 64);

    // The in-game fitting sim shows module state by colour: green active, red overheated, neutral grey online, dimmed
    // offline — not by slot category. Border + glyph use the state colour; the cell gets a faint translucent tint of it.
    public IBrush Color => new SolidColorBrush(Avalonia.Media.Color.Parse(StateBorderColor(State)));
    public IBrush FillBrush => new SolidColorBrush(Avalonia.Media.Color.Parse(StateFillColor(State)));
    public double TileOpacity => State == ModuleState.Passive ? 0.3 : 1.0;   // offline modules dim out (EVE)
    // Damage-type colours for the tooltip's damage line, matching the DEFENSE panel's resist bars (opaque variants).
    private static readonly IBrush EmBrush = Brush.Parse("#4E8AD9");
    private static readonly IBrush ThermalBrush = Brush.Parse("#D9544E");
    private static readonly IBrush KineticBrush = Brush.Parse("#9AA7B0");
    private static readonly IBrush ExplosiveBrush = Brush.Parse("#D9A441");

    /// <summary>Hands this slot its own resolved per-module contribution so the tooltip can show the in-game
    /// readout (DPS + damage types + range/tracking, mining yield, rep/s, cap/s). Null falls back to name + state.</summary>
    public void SetContribution(ModuleContribution? contribution)
    {
        _contribution = contribution;
        OnPropertyChanged(nameof(TooltipModel));
    }

    // The in-game per-module tooltip: name, loaded charge, the type-specific derived lines,
    // a colour-coded damage breakdown and a state line coloured by state. Built from the resolved contribution so the
    // lines match the same numbers as the panels.
    public ModuleTooltipViewModel TooltipModel => BuildTooltip();

    private ModuleTooltipViewModel BuildTooltip()
    {
        var lines = new List<string>();
        var damage = new List<DamageSegmentViewModel>();
        string? chargeName = null;

        if (ChargeTypeId is { } chargeId
            && _chargeOptions.FirstOrDefault(option => option.TypeId == chargeId) is { } charge)
            chargeName = charge.Name;

        if (_contribution is { } contribution)
        {
            switch (contribution.Kind)
            {
                case ModuleContributionKind.Turret:
                case ModuleContributionKind.Drone:
                    AppendRange(lines, contribution);
                    lines.Add(DpsLine(contribution));
                    AppendDamage(damage, contribution);
                    if (contribution.TrackingSpeed > 0)
                        lines.Add($"Tracking {Num(contribution.TrackingSpeed, 3)}");
                    break;
                case ModuleContributionKind.Missile:
                    if (RoundsAboveZeroKm(contribution.OptimalRange))
                        lines.Add($"Range {Km(contribution.OptimalRange)} km");
                    lines.Add(DpsLine(contribution));
                    AppendDamage(damage, contribution);
                    break;
                case ModuleContributionKind.Mining:
                    if (RoundsAboveZeroKm(contribution.OptimalRange))
                        lines.Add($"Optimal {Km(contribution.OptimalRange)} km");
                    lines.Add($"{Num(contribution.M3PerCycle, 0)} m³ per cycle ({Num(contribution.MiningYieldPerSec, 1)} m³/s)");
                    break;
                case ModuleContributionKind.Propulsion:
                    lines.Add($"+{Num(contribution.SpeedBoostPercent, 0)}% max velocity");
                    break;
                case ModuleContributionKind.LocalRepair:
                    lines.Add($"{Num(contribution.RepPerSec, 1)} HP/s {RepairLabel(contribution.RepairLayer)}");
                    break;
                case ModuleContributionKind.Capacitor:
                    lines.Add($"{Num(contribution.CapPerSec, 1)} GJ/s capacitor");
                    break;
                case ModuleContributionKind.RemoteRepair:
                    lines.Add($"{Num(contribution.RepPerSec, 1)} HP/s {RepairLabel(contribution.RepairLayer)} (remote, {Km(contribution.RemoteRangeMeters)} km)");
                    break;
                case ModuleContributionKind.RemoteCapTransfer:
                    lines.Add($"{Num(contribution.CapPerSec, 1)} GJ/s capacitor (remote, {Km(contribution.RemoteRangeMeters)} km)");
                    break;
            }
        }

        return new ModuleTooltipViewModel
        {
            Name = Name,
            SlotLabel = SlotLabel,
            ChargeName = chargeName,
            Lines = lines,
            DamageSegments = damage,
            StateLabel = StateLabel(State),
            StateBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(StateBorderColor(State)))
        };
    }

    // Optimal and falloff each on their own line (in-game, ref tooltips/06), and only when they round to a non-zero km —
    // a Triglavian disintegrator has no falloff, so a 0.0 km falloff line is suppressed rather than shown.
    private static void AppendRange(List<string> lines, ModuleContribution contribution)
    {
        if (RoundsAboveZeroKm(contribution.OptimalRange))
            lines.Add($"Optimal {Km(contribution.OptimalRange)} km");
        if (RoundsAboveZeroKm(contribution.FalloffRange))
            lines.Add($"Falloff {Km(contribution.FalloffRange)} km");
    }

    private static bool RoundsAboveZeroKm(double meters) => Math.Round(meters / 1000.0, 1) > 0;

    // An entropic disintegrator ramps its DPS up over its spool cycles, so it reads as a base–max range; every other
    // weapon has DpsMax equal to Dps and reads as a single value.
    private static string DpsLine(ModuleContribution contribution) =>
        contribution.DpsMax > contribution.Dps + 0.05
            ? $"Damage Per Second {Num(contribution.Dps, 1)} – {Num(contribution.DpsMax, 1)}"
            : $"Damage Per Second {Num(contribution.Dps, 1)}";

    private static void AppendDamage(List<DamageSegmentViewModel> segments, ModuleContribution contribution)
    {
        if (contribution.DamageEm > 0)
            segments.Add(new DamageSegmentViewModel($"EM {Num(contribution.DamageEm, 0)}", EmBrush));
        if (contribution.DamageThermal > 0)
            segments.Add(new DamageSegmentViewModel($"TH {Num(contribution.DamageThermal, 0)}", ThermalBrush));
        if (contribution.DamageKinetic > 0)
            segments.Add(new DamageSegmentViewModel($"KIN {Num(contribution.DamageKinetic, 0)}", KineticBrush));
        if (contribution.DamageExplosive > 0)
            segments.Add(new DamageSegmentViewModel($"EXP {Num(contribution.DamageExplosive, 0)}", ExplosiveBrush));
    }

    private static string Km(double meters) => Num(meters / 1000.0, 1);
    private static string Num(double value, int digits) => value.ToString("F" + digits, CultureInfo.InvariantCulture);

    // EVE slot grouping abbreviation for the tooltip's slot-position label (we have no in-game hotkey to surface, so the
    // slot's category + ordinal stands in: "HIGH 1", "MID 2", …).
    private static string _SlotAbbrev(FitSlotCategory category) => category switch
    {
        FitSlotCategory.High => "HIGH",
        FitSlotCategory.Medium => "MID",
        FitSlotCategory.Low => "LOW",
        FitSlotCategory.Rig => "RIG",
        FitSlotCategory.Subsystem => "SUB",
        FitSlotCategory.Service => "SERVICE",
        _ => category.ToString().ToUpperInvariant()
    };

    private static string RepairLabel(RepairLayer layer) => layer switch
    {
        RepairLayer.Shield => "shield boosted",
        RepairLayer.Armor => "armor repaired",
        RepairLayer.Hull => "hull repaired",
        _ => "repaired"
    };

    [RelayCommand]
    private async Task CycleState()
    {
        var index = Array.IndexOf(_validStates, State);
        var next = _validStates[(index + 1) % _validStates.Length];   // offline → online → active → overloaded → offline
        if (next >= ModuleState.Active && _approveActivation is { } approve && !approve(this, next))
            return;   // activation refused (e.g. a cloak conflict); the owner surfaced the reason, keep the current state
        State = next;
        await _onChanged();
    }

    /// <summary>Whether this module accepts the given charge type (drag-and-drop drop-target check, 2f).</summary>
    public bool AcceptsCharge(int chargeTypeId) => _chargeOptions.Any(charge => charge.TypeId == chargeTypeId);

    /// <summary>Loads a charge dropped onto this module (2f); no-op when the module does not accept it.</summary>
    public Task LoadChargeAsync(int chargeTypeId) => AcceptsCharge(chargeTypeId) ? SetChargeAsync(chargeTypeId) : Task.CompletedTask;

    private async Task SetChargeAsync(int? chargeTypeId)
    {
        ChargeTypeId = chargeTypeId;   // null = remove
        await _onChanged();            // DPS/cap/resource recompute via ToInput.ChargeTypeId
        await LoadImageAsync();        // swap to the charge icon (or back to the module icon)
    }

    // Rebuild the menu whenever the charge changes so Charge Information + Remove appear only while a charge is loaded.
    partial void OnChargeTypeIdChanged(int? value) => ChargeMenu = BuildMenu();

    private List<ChargeMenuOptionViewModel> BuildMenu()
    {
        var menu = new List<ChargeMenuOptionViewModel>();

        if (_onShowInfo is not null)
        {
            menu.Add(new ChargeMenuOptionViewModel("ℹ  Information", new RelayCommand(() => _ = _onShowInfo(TypeId))));
            if (ChargeTypeId is { } loaded)
                menu.Add(new ChargeMenuOptionViewModel("ℹ  Charge Information", new RelayCommand(() => _ = _onShowInfo(loaded))));
        }

        if (ChargeTypeId is not null)
            menu.Add(new ChargeMenuOptionViewModel("✕  Remove charge", new RelayCommand(() => _ = SetChargeAsync(null))));

        // Nest the compatible charges under a single "Charges" submenu (only when the module accepts any).
        if (_chargeOptions.Count > 0)
        {
            var charges = _chargeOptions
                .Select(charge =>
                {
                    var id = charge.TypeId;
                    return new ChargeMenuOptionViewModel(charge.Name, new RelayCommand(() => _ = SetChargeAsync(id)));
                })
                .ToList();
            menu.Add(new ChargeMenuOptionViewModel("Charges", children: charges));
        }

        return menu;
    }

    public static string StateLabel(ModuleState state) => state switch
    {
        ModuleState.Passive => "Offline",
        ModuleState.Online => "Online",
        ModuleState.Active => "Active",
        ModuleState.Overload => "Overheated",
        _ => state.ToString()
    };

    // EVE fitting-sim state palette (research-confirmed): active #8AE04A, overheat #FD2D2D, online neutral grey, offline dim.
    private static string StateBorderColor(ModuleState state) => state switch
    {
        ModuleState.Passive => "#6E7681",     // offline: muted grey (the tile also dims via TileOpacity)
        ModuleState.Active => "#8AE04A",      // green: active
        ModuleState.Overload => "#FD2D2D",    // red: overheated
        _ => "#9BA6B0"                        // online: neutral grey, no slot-category tint (matches EVE)
    };

    // A faint translucent wash of the state colour behind the icon (active/overheat only); online/offline stay glassy.
    private static string StateFillColor(ModuleState state) => state switch
    {
        ModuleState.Active => "#338AE04A",
        ModuleState.Overload => "#33FD2D2D",
        _ => "#00000000"
    };
}
