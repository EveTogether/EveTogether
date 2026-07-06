using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fittings;
using EveUtils.Client.Notifications;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Client.Skills;
using EveUtils.Client.Implants;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Repositories;
using EveUtils.Shared.Modules.Implants.Repositories;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Fighters;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// The radial fit-detail window: a fitting wheel of interactive module boxes around the ship plus the
/// Dogma-computed stats panels (firepower / resource / resistance / capacitor / targeting / navigation / drones).
/// Clicking a module box cycles its activation state (offline/online/active/overloaded) and recomputes the stats live
/// through <see cref="IFitStatsProvider"/>; a null provider/SDE means the engine was unavailable and the window shows a
/// notice. The formatted stat strings read off the latest <see cref="FitStats"/>, so a recompute refreshes every panel.
/// </summary>
public sealed class FitDetailWindowViewModel : ViewModelBase
{
    // Radial geometry (the canvas is a fixed square; slots sit on a ring around the centre).
    public const double CanvasSize = 380;
    private const double Center = CanvasSize / 2;
    // The slot tiles are curved annular segments tiling the framing ring (radius ~140–184): outer/inner edges follow
    // these radii, so the tiles hug the ring like the in-game / eveship wheel instead of upright rectangles.
    private const double SlotOuter = 165;
    private const double SlotInner = 139;
    private const double SlotGapDeg = 1.4;    // angular gap between adjacent tiles so they read as separate slots
    private const double IconSize = 22;       // the upright module icon centred in each segment
    // CPU / PG / Calibration gauges over the solid black outer ring (EVEShipFit / in-game look, validated 2026-06-14):
    // a coloured fill arc with a brighter core line, white ticks (brighter on the used part) and a few larger markers.
    // Geometry scaled from a reference layout (centre 256, radius 226, band 9) to this wheel (centre 190, radius 185, band 7.5).
    private const double GaugeRadius = 179;   // just inside the black outer ring, a small margin off the module-slot ring
    private const double GaugeBand = 7.5;     // fill-arc thickness; ticks sit just inside/outside it (reference BAND)

    // Turret / launcher hardpoint indicators, mirroring the in-game wheel: the dots sit just
    // outside the high-slot tiles — the FIRST turret dot is aligned with the FIRST high slot and the dots step close together
    // toward 12 o'clock; the launcher dots mirror that from the LAST high slot. Grey: filled = fitted, hollow ring = free.
    private const double HardpointRadius = 180;          // just above the slot-tile ring (close to the tiles, not on them)
    private const double HardpointPipSize = 5;
    private const double HardpointDotSpacingDeg = 3.5;   // step between dots — close together (less than the 10° slot step)
    // The dot's angular half-size, so its outer EDGE (not centre) can sit exactly on a slot's outer edge.
    private const double HardpointDotAngularRadius = HardpointPipSize / 2 / HardpointRadius * 180.0 / Math.PI;
    // A turret/launcher label glyph just outside each cluster's first dot (the in-game hardpoint-type labels).
    private const double HardpointIconSize = 11;
    private const double HardpointIconGapDeg = 1.0;      // gap between a cluster's first dot and its label icon (hugs the dots)
    private const double HardpointIconAngularRadius = HardpointIconSize / 2 / HardpointRadius * 180.0 / Math.PI;

    private static readonly IReadOnlyDictionary<FitSlotCategory, string> Glyphs = new Dictionary<FitSlotCategory, string>
    {
        [FitSlotCategory.High] = "H",
        [FitSlotCategory.Medium] = "M",
        [FitSlotCategory.Low] = "L",
        [FitSlotCategory.Rig] = "R",
        [FitSlotCategory.Subsystem] = "S",
        [FitSlotCategory.Service] = "Sv",
    };

    private static readonly FitSlotCategory[] SlotOrder =
        [FitSlotCategory.High, FitSlotCategory.Medium, FitSlotCategory.Low, FitSlotCategory.Rig,
            FitSlotCategory.Subsystem, FitSlotCategory.Service];

    // Fixed arc anchors per slot category — start angle + fixed step per slot, angles in EVEShipFit convention
    // (0° = top, clockwise positive). Cross-validated against both reference implementations:
    // high top→upper-right, mid right→lower-right, low bottom→lower-left, rig left, subsystem (T3C) upper-left.
    private static readonly IReadOnlyDictionary<FitSlotCategory, (double Start, double Step)> ArcLayout =
        new Dictionary<FitSlotCategory, (double, double)>
        {
            [FitSlotCategory.High] = (-36.5, 10.0),
            [FitSlotCategory.Medium] = (53.0, 10.0),
            [FitSlotCategory.Low] = (142.0, 10.3),
            [FitSlotCategory.Rig] = (-74.0, 10.5),
            [FitSlotCategory.Subsystem] = (-128.0, 12.7),
            // Structures have no subsystems, so the upper-left zone is free for the defining service-slot ring (attr 2056).
            // Placement is unconfirmed for structures (the radial research only covered ships) — render-verify.
            [FitSlotCategory.Service] = (-120.0, 12.0),
        };

    // Hull dogma attribute id per slot category, used to draw the full ring including empty placeholders.
    private static readonly IReadOnlyDictionary<FitSlotCategory, int> SlotCountAttribute = new Dictionary<FitSlotCategory, int>
    {
        [FitSlotCategory.High] = 14,
        [FitSlotCategory.Medium] = 13,
        [FitSlotCategory.Low] = 12,
        [FitSlotCategory.Rig] = 1137,
        [FitSlotCategory.Service] = 2056,   // serviceSlots — Upwell structures (Raitaru = 3)
    };

    // The universal maximum slots a ship can have per category (EVE caps): the ring always lays out this many positions
    // so it reads as a full, uniform ring (in-game) — the hull's actual slots are filled/empty, the remainder are
    // placeholders. Structures (service slots) have no fixed cap, so they fall back to the hull count.
    private static readonly IReadOnlyDictionary<FitSlotCategory, int> MaxSlots = new Dictionary<FitSlotCategory, int>
    {
        [FitSlotCategory.High] = 8,
        [FitSlotCategory.Medium] = 8,
        [FitSlotCategory.Low] = 8,
        [FitSlotCategory.Subsystem] = 4,
        [FitSlotCategory.Rig] = 3,
    };

    // The SDE category id for Upwell structures (Type -> Group -> Category); drives the structure-variant gating.
    private const int StructureCategoryId = 65;

    private readonly EsiFitting _fit;
    private readonly IFitStatsProvider? _provider;
    private readonly ITypeImageProvider? _images;
    private readonly IMarketPriceRepository? _prices;
    private readonly ISdeAccessor? _sde;
    private readonly ISdeNameResolver _names;
    private readonly IToastService? _toasts;   // surfaces a refused module activation (cloak conflict); null in headless tests
    private readonly Func<int, Task>? _onShowInfo;
    private FitStats? _stats;

    // Fit validation: the validator (over the Dogma data) plus the selected character's trained skill levels
    // (null = an all-skills baseline, so no skill gaps are reported). Recomputed with the stats; drives the
    // "Skills Required" panel, the fitting-alert banner and the header badges.
    private readonly IFitValidator? _validator;
    private IReadOnlyDictionary<int, int>? _trainedSkills;
    private FitValidationResult _validation = FitValidationResult.Empty;

    // Skill-gap SP + Omega training-time estimate: the selected character's effective attributes (base + the
    // attribute implants) drive the per-skill rate, so the estimate shortens with +stat implants.
    private readonly ICharacterAttributesRepository? _attributesRepository;
    private readonly CharacterAttributeResolver? _attributeResolver;
    private readonly SkillTrainingEstimator? _trainingEstimator;
    private CharacterAttributeSet? _effectiveAttributes;
    private readonly double _calibrationUsed;
    private readonly double _calibrationTotal;
    private readonly int _turretHardpointsUsed;
    private readonly int _turretHardpointsTotal;
    private readonly int _launcherHardpointsUsed;
    private readonly int _launcherHardpointsTotal;
    private Bitmap? _shipImage;
    private string _iskValue = "— ISK";
    private int? _selectedModeTypeId;
    private readonly bool _isStructure;   // an Upwell structure (category 65) — drives the structure-variant gating

    private const int MaxActiveDrones = 5;          // the universal in-space drone limit
    private const int ShipDroneBandwidthAttr = 1271;
    private const int DroneBandwidthUseAttr = 1272;
    private readonly double _shipDroneBandwidth;     // 0 when unknown (no engine data) -> only the 5-drone cap applies

    // Fighter-bay ship attributes: total launch tubes, the per-kind tube limits and the bay capacity (m³).
    private const int FighterTubesAttr = 2216;
    private const int FighterLightSlotsAttr = 2217;
    private const int FighterSupportSlotsAttr = 2218;
    private const int FighterHeavySlotsAttr = 2219;
    private const int FighterBayCapacityAttr = 2055;

    // Structure fuel (D): a service module draws serviceModuleFuelAmount fuel blocks per hour while online (a one-hour
    // cycle), with serviceModuleFuelOnlineAmount as the one-off cost to bring it online.
    private const int ServiceModuleFuelAmountAttr = 2109;
    private const int ServiceModuleOnlineFuelAttr = 2110;
    private const int StructureServiceRoleBonusAttr = 2339;   // hull role bonus to service-module fuel, e.g. Raitaru -25 (%)
    private readonly IReadOnlyList<FuelServiceModule> _serviceModules;
    private readonly double _fuelBayCapacity;        // hard-coded CCP value per structure (0 = unknown -> no runtime)

    private readonly IEsiSkillImporter? _skillImporter;
    private readonly ICharacterSkillRepository? _skillRepository;
    private readonly string? _rememberedSkillMode;          // persisted last-used mode, e.g. "all:5" / "char:42"
    private readonly Func<string, Task>? _onSkillModeChanged;
    private SkillSource? _activeSkills;              // null = all-level-5 baseline
    private string _skillStatus = "All V";
    private SkillModeViewModel? _selectedSkillMode;
    private bool _suppressSkillApply;               // true while we set SelectedSkillMode programmatically (no re-apply)

    private readonly IEsiImplantImporter? _implantImporter;
    private readonly ICharacterImplantRepository? _implantRepository;
    private readonly string? _rememberedImplantMode;        // persisted last-used implant source, e.g. "fit" / "char:42"
    private readonly Func<string, Task>? _onImplantModeChanged;
    private ImplantSource _activeImplants = ImplantSource.FromFit;   // default to the fit's own implants
    private string _implantStatus = "Fit implants";

    // damage-profile selector — the DEFENSE panel picker; null before the ViewModel is fully constructed.
    private DamageProfileSelectorViewModel? _damageProfileSelector;

    // weather/environment selector — the header picker; null before the ViewModel is fully constructed.
    private WeatherSelectorViewModel? _weatherSelector;
    private ImplantModeViewModel? _selectedImplantMode;
    private bool _suppressImplantApply;             // true while we set SelectedImplantMode programmatically

    public string Name { get; private set; }
    public string ShipName { get; }

    // ── Fit-metadata: the user's notes + tags. Shown in the header and editable in place via the Edit-details
    // button (which reuses the same dialog + repo flow as the fit-browser overflow menu); the header refreshes on a successful edit. ──

    /// <summary>The user's free-text notes for this fit, or null when none — shown under the hull name.</summary>
    public string? Description { get; private set; }

    public bool HasDescription => Description is not null;

    /// <summary>The user's tags as chips (parsed from the comma-separated metadata), empty when none.</summary>
    public IReadOnlyList<string> TagChips { get; private set; }

    public bool HasTags => TagChips.Count > 0;

    public IReadOnlyList<ModuleSlotViewModel> RadialSlots { get; }

    /// <summary>Dim placeholder hexes for the hull's unfilled slots, so the ring is continuous like eveship.fit
    /// (empty when the hull's slot counts are unknown).</summary>
    public IReadOnlyList<FitRadialSlotViewModel> EmptySlots { get; }

    /// <summary>Compatible charges across the fit's modules (2f), listed in the left-hand Charges panel (the in-game
    /// Charges tab): drag one onto a module to load it there, or onto the wheel centre to load it on every module that
    /// accepts it. The panel is inline (not a flyout) so the drag delivers its drop.</summary>
    public IReadOnlyList<DraggableChargeViewModel> AvailableCharges { get; }

    /// <summary>The fit's charge-capable modules, listed in the left Charges panel as a per-module picker: each row sets
    /// its own charge from a dropdown of the charges it accepts (in-game-style per-module distribution). Same instances
    /// as <see cref="RadialSlots"/>, so a pick updates the wheel and the stats together.</summary>
    /// <summary>Charge-capable modules grouped by type (identical turrets become one filter icon) for the top of the
    /// Charges panel; clicking one filters the list below to that type's charges.</summary>
    public IReadOnlyList<ChargeModuleGroupViewModel> ChargeModuleGroups { get; }

    private ChargeModuleGroupViewModel? _selectedChargeGroup;

    /// <summary>The module type the Charges panel is filtered to: its icon is selected at the top, the charge list below
    /// shows only the charges it accepts. Defaults to the first group.</summary>
    public ChargeModuleGroupViewModel? SelectedChargeGroup
    {
        get => _selectedChargeGroup;
        set
        {
            var previous = _selectedChargeGroup;
            if (!SetProperty(ref _selectedChargeGroup, value))
                return;
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged(nameof(SelectedModuleCharges));
        }
    }

    /// <summary>The charges of <see cref="SelectedChargeGroup"/> (icons already loaded with <see cref="AvailableCharges"/>);
    /// drag one onto a module (or the wheel centre) to load it — clicking a row does nothing (2026-06-08: place by drag only).</summary>
    public IReadOnlyList<DraggableChargeViewModel> SelectedModuleCharges =>
        _selectedChargeGroup is null
            ? []
            : AvailableCharges.Where(charge => _selectedChargeGroup.AcceptsCharge(charge.TypeId)).ToList();

    /// <summary>Filters the charge list to a module type (clicking its icon at the top of the Charges panel).</summary>
    public IRelayCommand<ChargeModuleGroupViewModel> SelectChargeModuleCommand { get; }

    /// <summary>Loads a charge onto every module that accepts it (the wheel-centre drop, 2f); a no-op on modules that don't.</summary>
    public async Task LoadChargeOnAllAsync(int chargeTypeId)
    {
        foreach (var slot in RadialSlots)
            await slot.LoadChargeAsync(chargeTypeId);
    }

    /// <summary>Drone stacks in the bay; deploy/recall on each one drives which drones are "in space" and the drone DPS.</summary>
    public IReadOnlyList<DroneBayItemViewModel> DroneBay { get; }

    /// <summary>The Fighter Bay panel (carriers/supercarriers/structures): launch tubes, per-kind limits, the reserve
    /// list and the per-squadron active steppers. Null when the ship has no fighter tubes, which hides the panel.</summary>
    public FighterBayViewModel? FighterBay { get; }

    /// <summary>True when the ship carries fighter launch tubes, so the Fighter Bay panel is shown.</summary>
    public bool HasFighterBay => FighterBay is not null;

    /// <summary>"deployed / 5" — the universal active-drone limit, shown in the Drone Bay header.</summary>
    public string DroneActiveLabel => $"{DroneBay.Sum(drone => drone.ActiveQuantity)} / {MaxActiveDrones}";

    /// <summary>Combat boosters the user is simulating (not stored in the fit); each applied as an implant when active.</summary>
    public ObservableCollection<BoosterViewModel> Boosters { get; } = [];
    public bool HasBoosters => Boosters.Count > 0;

    /// <summary>The "+ add booster" picker entries (every SDE booster type); empty when the SDE is unavailable.</summary>
    public IReadOnlyList<ChargeMenuOptionViewModel> BoosterMenu { get; private set; } = [];
    public bool HasBoosterPicker => BoosterMenu.Count > 0;

    /// <summary>Skills dropdown entries: the All I..V baselines plus each coupled character.</summary>
    public IReadOnlyList<SkillModeViewModel> SkillModes { get; }
    public bool HasSkillCharacters => SkillModes.Any(mode => mode.CharacterId is not null);
    public string SkillStatus { get => _skillStatus; private set => SetProperty(ref _skillStatus, value); }

    /// <summary>The dropdown's selected mode; picking one applies it and recomputes (programmatic sets are suppressed).</summary>
    public SkillModeViewModel? SelectedSkillMode
    {
        get => _selectedSkillMode;
        set
        {
            if (SetProperty(ref _selectedSkillMode, value) && value is not null && !_suppressSkillApply)
                _ = SelectSkillModeAsync(value);
        }
    }

    /// <summary>Implant-source picker entries: the fit's own implants, plus each coupled character.</summary>
    public IReadOnlyList<ImplantModeViewModel> ImplantModes { get; } = [];
    public bool HasImplantCharacters => ImplantModes.Any(mode => mode.CharacterId is not null);
    public string ImplantStatus { get => _implantStatus; private set => SetProperty(ref _implantStatus, value); }

    /// <summary>The implants currently applied to the fit (icon + name), read-only — they come from the selected source.</summary>
    public ObservableCollection<ImplantSlotViewModel> Implants { get; } = [];
    public bool HasImplants => Implants.Count > 0;

    /// <summary>damage-profile selector for the DEFENSE panel (Presets / Custom / NPC modes).</summary>
    public DamageProfileSelectorViewModel? DamageProfileSelector => _damageProfileSelector;

    /// <summary>weather/environment selector — the header dropdown of effect beacons applied to the fit.</summary>
    public WeatherSelectorViewModel? WeatherSelector => _weatherSelector;

    /// <summary>The selected implant source; picking one applies it and recomputes (programmatic sets are suppressed).</summary>
    public ImplantModeViewModel? SelectedImplantMode
    {
        get => _selectedImplantMode;
        set
        {
            if (SetProperty(ref _selectedImplantMode, value) && value is not null && !_suppressImplantApply)
                _ = SelectImplantModeAsync(value);
        }
    }

    /// <summary>Item stacks in the fit's cargo hold (icon + quantity), shown next to the drone bay.</summary>
    public IReadOnlyList<CargoItemViewModel> CargoItems { get; }

    /// <summary>A Tactical Destroyer's stance modes; empty for other ships.</summary>
    public IReadOnlyList<TacticalModeViewModel> TacticalModes { get; }
    public bool HasTacticalModes => TacticalModes.Count > 0;

    /// <summary>The ship render shown in the wheel centre; null until loaded / when images are disabled, so the
    /// offline silhouette stays.</summary>
    public Bitmap? ShipImage
    {
        get => _shipImage;
        private set { if (SetProperty(ref _shipImage, value)) OnPropertyChanged(nameof(HasShipImage)); }
    }

    public bool HasShipImage => _shipImage is not null;

    public FitDetailWindowViewModel(EsiFitting fit, ISdeNameResolver names, IFitStatsProvider? provider,
        ISdeAccessor? sde, IDogmaDataAccessor? data, ITypeImageProvider? images = null, IMarketPriceRepository? prices = null,
        Func<int, Task>? onShowInfo = null, IReadOnlyList<(int Id, string Name)>? characters = null,
        IEsiSkillImporter? skillImporter = null, ICharacterSkillRepository? skillRepository = null,
        string? rememberedSkillMode = null, Func<string, Task>? onSkillModeChanged = null,
        IEsiImplantImporter? implantImporter = null, ICharacterImplantRepository? implantRepository = null,
        string? rememberedImplantMode = null, Func<string, Task>? onImplantModeChanged = null,
        IFitExportActions? exportActions = null, int? localFitId = null,
        Func<string, IReadOnlyList<CharacterPickOption>>? exportPickOptions = null,
        string? description = null, string? tags = null,
        ICharacterAttributesRepository? attributesRepository = null, IToastService? toasts = null,
        Func<int, Task<FitMetadataDraft?>>? onEditMetadata = null)
    {
        _fit = fit;
        _onEditMetadata = onEditMetadata;
        Description = _NormalizeDescription(description);
        TagChips = _ParseTags(tags);
        _attributesRepository = attributesRepository;
        _attributeResolver = data is null ? null : new CharacterAttributeResolver(data);
        _trainingEstimator = data is null ? null : new SkillTrainingEstimator(data);
        _provider = provider;
        _images = images;
        _prices = prices;
        _sde = sde;
        _names = names;
        _toasts = toasts;
        _onShowInfo = onShowInfo;
        _validator = data is null ? null : new FitValidator(data);   // validate skills + resource budgets
        Name = fit.Name;
        ShipName = names.TypeName(fit.ShipTypeId);
        _isStructure = data?.GetCategoryId(fit.ShipTypeId) == StructureCategoryId;

        (RadialSlots, EmptySlots, AvailableCharges) = BuildSlots(fit, names, sde, data);
        ChargeModuleGroups = RadialSlots.Where(slot => slot.CanLoadCharge)
            .GroupBy(slot => slot.TypeId)
            .Select(group => new ChargeModuleGroupViewModel(group.ToList(), _images))
            .ToList();
        SelectChargeModuleCommand = new RelayCommand<ChargeModuleGroupViewModel>(group => { if (group is not null) SelectedChargeGroup = group; });
        SelectedChargeGroup = ChargeModuleGroups.FirstOrDefault();   // default to the first type so the panel isn't empty
        (_calibrationUsed, _calibrationTotal) = FitResourceMath.Calibration(fit, data);
        (_turretHardpointsUsed, _turretHardpointsTotal, _launcherHardpointsUsed, _launcherHardpointsTotal) =
            Hardpoints(fit, sde, data);

        // Structure fuel: each service-slot module draws fuel while online; the bay capacity gives a runtime estimate.
        // The hull's structureServiceRoleBonus (attr 2339, e.g. a Raitaru's -25) reduces each service module's fuel
        // need by that percentage. SDE effect 6759 (engComplexServiceFuelBonus) modifies both the hourly draw (2109)
        // and the online cost (2110), operation 6 (postPercent), so -25 -> x0.75. One role bonus -> no stacking penalty.
        var serviceFuelFactor = 1.0 + BaseAttribute(data, fit.ShipTypeId, StructureServiceRoleBonusAttr) / 100.0;
        _serviceModules = _isStructure
            ? RadialSlots
                .Where(slot => slot.Category == FitSlotCategory.Service)
                .Select(slot => new FuelServiceModule(slot,
                    BaseAttribute(data, slot.TypeId, ServiceModuleFuelAmountAttr) * serviceFuelFactor,
                    BaseAttribute(data, slot.TypeId, ServiceModuleOnlineFuelAttr) * serviceFuelFactor))
                .ToList()
            : [];
        _fuelBayCapacity = _isStructure ? StructureFuelBay.CapacityInBlocks : 0;

        _shipDroneBandwidth = BaseAttribute(data, fit.ShipTypeId, ShipDroneBandwidthAttr);
        DroneBay = fit.Items
            .Where(item => FitSlotClassifier.Classify(item.Flag) == FitSlotCategory.Drone)
            .GroupBy(item => item.TypeId)
            .Select(group => new DroneBayItemViewModel(group.Key, names.TypeName(group.Key),
                group.Sum(item => item.Quantity), BaseAttribute(data, group.Key, DroneBandwidthUseAttr), _images, SetDroneActiveAsync))
            .ToList();
        DeployDefaultDrones();   // start with the strongest-fitting drones in space (the engine's auto-deploy mirror)

        var fighterAccessor = sde is not null ? new FighterAccessor(sde) : null;
        FighterBay = BuildFighterBay(fit, fighterAccessor, data);

        // Fighter-type items the import dropped in the cargo hold (EFT/ESI often store a carrier's fighters there) belong
        // in the Fighter Bay, not cargo — exclude them here so they show as squadrons in the bay, not doubled in CARGO.
        CargoItems = fit.Items
            .Where(item => FitSlotClassifier.Classify(item.Flag) == FitSlotCategory.Cargo
                           && fighterAccessor?.GetFighterType(item.TypeId) is null)
            .GroupBy(item => item.TypeId)
            .Select(group => new CargoItemViewModel(group.Key, names.TypeName(group.Key), group.Sum(item => item.Quantity), _images))
            .ToList();

        StorageBays = BuildStorageBays(data);

        TacticalModes = BuildTacticalModes(data, fit.ShipTypeId);
        BoosterMenu = BuildBoosterMenu(sde);

        _skillImporter = skillImporter;
        _skillRepository = skillRepository;
        _rememberedSkillMode = rememberedSkillMode;
        _onSkillModeChanged = onSkillModeChanged;
        var modes = new List<SkillModeViewModel>();
        for (var level = 1; level <= 5; level++)   // all-skills baselines I..V
            modes.Add(new SkillModeViewModel(null, level, $"All {RomanLevels[level]}"));
        if (characters is not null && skillImporter is not null && skillRepository is not null)
            modes.AddRange(characters.Select(c => new SkillModeViewModel(c.Id, 0, c.Name)));
        SkillModes = modes;
        _selectedSkillMode = modes.First(mode => mode.CharacterId is null && mode.AllLevel == 5);   // default All V

        _implantImporter = implantImporter;
        _implantRepository = implantRepository;
        _rememberedImplantMode = rememberedImplantMode;
        _onImplantModeChanged = onImplantModeChanged;
        var implantModes = new List<ImplantModeViewModel> { new(null, "Fit") };   // the fit's own implants
        if (characters is not null && implantImporter is not null && implantRepository is not null)
            implantModes.AddRange(characters.Select(character => new ImplantModeViewModel(character.Id, character.Name)));
        ImplantModes = implantModes;
        _selectedImplantMode = implantModes[0];   // default to the fit's own implants

        // damage-profile selector — built after _sde is set; subscribes to ProfileChanged to recompute.
        _damageProfileSelector = new DamageProfileSelectorViewModel(sde);
        _damageProfileSelector.ProfileChanged += (_, _) => _ = RecomputeAsync();

        // weather/environment selector — built from the SDE effect beacons; recomputes on a new selection.
        _weatherSelector = new WeatherSelectorViewModel(sde);
        _weatherSelector.WeatherChanged += (_, _) => _ = RecomputeAsync();

        // the four export actions reach the shared seam. Disabled when this fit has no local DB id
        // (a server-shared fit that has not been downloaded), since push/share key off it.
        _exportActions = exportActions;
        _localFitId = localFitId;
        _exportPickOptions = exportPickOptions ?? (_ => Array.Empty<CharacterPickOption>());
        ShareToServerCommand   = new AsyncRelayCommand(() => InvokeExportAsync((a, r) => a.ShareToServerAsync(r)), () => CanExport);
        PushToEveCommand       = new AsyncRelayCommand(() => InvokeExportAsync((a, r) => a.PushToEveAsync(r)), () => CanExport);
        CopyEveshipLinkCommand = new AsyncRelayCommand(() => InvokeExportAsync((a, r) => a.CopyEveshipLinkAsync(r)), () => CanExport);
        OpenEftWindowCommand   = new AsyncRelayCommand(() => InvokeExportAsync((a, r) => a.OpenEftWindowAsync(r)), () => CanExport);
        EditMetadataCommand    = new AsyncRelayCommand(_EditMetadataAsync, () => CanEditMetadata);
    }

    // ── fit export (share / push / copy link / EFT window) via the shared seam ───────────
    private readonly IFitExportActions? _exportActions;
    private readonly int? _localFitId;
    private readonly Func<string, IReadOnlyList<CharacterPickOption>> _exportPickOptions;

    /// <summary>True when this fit can be exported — it has a local DB id and the seam is wired.</summary>
    public bool CanExport => _exportActions is not null && _localFitId is not null;

    public ICommand ShareToServerCommand { get; }
    public ICommand PushToEveCommand { get; }
    public ICommand CopyEveshipLinkCommand { get; }
    public ICommand OpenEftWindowCommand { get; }

    // ── Fit-metadata in-place edit: reuses the fit-browser's dialog+repo flow via a callback (so the services stay in
    // MainWindowViewModel), then refreshes this window's header from the edited draft. Local fits only. ──
    private readonly Func<int, Task<FitMetadataDraft?>>? _onEditMetadata;

    /// <summary>True when this fit's name/notes/tags can be edited here — it is a local fit and the edit seam is wired.</summary>
    public bool CanEditMetadata => _localFitId is not null && _onEditMetadata is not null;

    public ICommand EditMetadataCommand { get; }

    private async Task _EditMetadataAsync()
    {
        if (_onEditMetadata is null || _localFitId is null) return;
        if (await _onEditMetadata(_localFitId.Value) is { } edited)
            _ApplyMetadata(edited.Name, edited.Description, edited.Tags);
    }

    // Push an edited draft into the header and notify the bound properties (the name, the notes and the tag chips all
    // refresh in place without reopening the window).
    private void _ApplyMetadata(string name, string? description, string? tags)
    {
        Name = name;
        Description = _NormalizeDescription(description);
        TagChips = _ParseTags(tags);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(TagChips));
        OnPropertyChanged(nameof(HasTags));
    }

    private static string? _NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static string[] _ParseTags(string? tags) =>
        (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string _exportStatus = "";
    /// <summary>Human-readable feedback from the last export action (e.g. "Copied eveship.fit link …").</summary>
    public string ExportStatus
    {
        get => _exportStatus;
        private set { if (SetProperty(ref _exportStatus, value)) OnPropertyChanged(nameof(HasExportStatus)); }
    }

    public bool HasExportStatus => !string.IsNullOrEmpty(_exportStatus);

    private async Task InvokeExportAsync(Func<IFitExportActions, FitExportRequest, Task> action)
    {
        if (_exportActions is null || _localFitId is null) return;
        var request = new FitExportRequest(_localFitId.Value, Name, _exportPickOptions, status => ExportStatus = status);
        await action(_exportActions, request);
    }

    private static readonly string[] RomanLevels = ["", "I", "II", "III", "IV", "V"];

    // Switch the skill source and recompute. The all-V baseline needs no import; a character imports its skills
    // (snapshot + queue) fresh, falling back to the cached set when offline, or to all-V when nothing is available.
    public async Task SelectSkillModeAsync(SkillModeViewModel mode)
    {
        SetSelectedSkillMode(mode);                            // reflect in the dropdown without re-triggering apply
        if (_onSkillModeChanged is not null)
            await _onSkillModeChanged(EncodeSkillMode(mode));   // remember the last-used mode

        if (mode.CharacterId is not { } characterId)
        {
            _activeSkills = SkillSource.AllAtLevel(mode.AllLevel);
            _trainedSkills = null;   // all-level baseline -> no skill gaps
            SkillStatus = $"All level {mode.AllLevel}";
            await RecomputeAsync();
            return;
        }

        SkillStatus = $"Importing {mode.Label}…";
        var result = _skillImporter is null ? null : await _skillImporter.ImportAsync(characterId);
        var levels = _skillRepository is null
            ? (IReadOnlyDictionary<int, int>)new Dictionary<int, int>()
            : await _skillRepository.GetLevelsAsync(characterId);
        _trainedSkills = levels.Count > 0 ? levels : null;   // the character's trained levels drive the skill-gap check
        _effectiveAttributes = levels.Count > 0 ? await _ResolveEffectiveAttributesAsync(characterId) : null;   // SP/time rate

        if (levels.Count > 0)
        {
            _activeSkills = SkillSource.From(levels);
            SkillStatus = result is { IsSuccess: true }
                ? $"{mode.Label}: {levels.Count} skills"
                : $"{mode.Label}: cached {levels.Count} — {result?.Message}";
        }
        else
        {
            SelectAllVDefault();   // nothing usable -> fall back to the all-V baseline
            SkillStatus = result?.Message ?? "Skill import unavailable";
        }
        await RecomputeAsync();
    }

    // Restore the persisted skill mode on open — cache-only so opening a fit never fires a fresh ESI import.
    private async Task ApplyRememberedSkillModeAsync()
    {
        var mode = ResolveRememberedSkillMode();
        if (mode is null)
            return;
        SetSelectedSkillMode(mode);

        if (mode.CharacterId is not { } characterId)
        {
            _activeSkills = SkillSource.AllAtLevel(mode.AllLevel);
            _trainedSkills = null;   // all-level baseline -> no skill gaps
            SkillStatus = $"All level {mode.AllLevel}";
            return;
        }

        var levels = _skillRepository is null
            ? (IReadOnlyDictionary<int, int>)new Dictionary<int, int>()
            : await _skillRepository.GetLevelsAsync(characterId);
        _trainedSkills = levels.Count > 0 ? levels : null;   // the character's trained levels drive the skill-gap check
        _effectiveAttributes = levels.Count > 0 ? await _ResolveEffectiveAttributesAsync(characterId) : null;   // SP/time rate
        if (levels.Count > 0)
        {
            _activeSkills = SkillSource.From(levels);
            SkillStatus = $"{mode.Label}: {levels.Count} skills (cached)";
        }
        else
        {
            SelectAllVDefault();   // remembered character has no cached skills yet -> all-V until imported
        }
    }

    // The character's effective training attributes (base allocation + attribute implants) for the SP/time estimate
    // . Null when attributes or the SDE aren't available — the panel then shows the skill gaps without a rate.
    private async Task<CharacterAttributeSet?> _ResolveEffectiveAttributesAsync(int characterId)
    {
        if (_attributesRepository is null || _attributeResolver is null)
            return null;
        var baseAttributes = await _attributesRepository.GetAsync(characterId);
        if (baseAttributes is null)
            return null;
        var implantTypeIds = _implantRepository is null
            ? []
            : await _implantRepository.GetTypeIdsAsync(characterId);
        return _attributeResolver.Resolve(baseAttributes, implantTypeIds);
    }

    private SkillModeViewModel? ResolveRememberedSkillMode()
    {
        if (string.IsNullOrEmpty(_rememberedSkillMode))
            return null;
        var parts = _rememberedSkillMode.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var value))
            return null;
        return parts[0] switch
        {
            "all" => SkillModes.FirstOrDefault(mode => mode.CharacterId is null && mode.AllLevel == value),
            "char" => SkillModes.FirstOrDefault(mode => mode.CharacterId == value),
            _ => null
        };
    }

    private void SelectAllVDefault()
    {
        SetSelectedSkillMode(SkillModes.First(mode => mode.CharacterId is null && mode.AllLevel == 5));
        _activeSkills = null;
        _trainedSkills = null;   // all-V baseline -> no skill gaps
    }

    // Reflect a mode in the dropdown's SelectedSkillMode binding without re-triggering SelectSkillModeAsync.
    private void SetSelectedSkillMode(SkillModeViewModel mode)
    {
        _suppressSkillApply = true;
        SelectedSkillMode = mode;
        _suppressSkillApply = false;
    }

    private static string EncodeSkillMode(SkillModeViewModel mode) =>
        mode.CharacterId is { } characterId ? $"char:{characterId}" : $"all:{mode.AllLevel}";

    // Switch the implant source and recompute. "Fit" uses the fit's own implants (none added here yet); a
    // character imports its implants fresh, falling back to the cached set, and shows them in the IMPLANTS panel.
    public async Task SelectImplantModeAsync(ImplantModeViewModel mode)
    {
        SetSelectedImplantMode(mode);
        if (_onImplantModeChanged is not null)
            await _onImplantModeChanged(EncodeImplantMode(mode));   // remember the last-used source

        if (mode.CharacterId is not { } characterId)
        {
            _activeImplants = ImplantSource.FromFit;
            Implants.Clear();
            OnPropertyChanged(nameof(HasImplants));
            ImplantStatus = "Fit implants";
            await RecomputeAsync();
            return;
        }

        ImplantStatus = $"Importing {mode.Label}…";
        var result = _implantImporter is null ? null : await _implantImporter.ImportAsync(characterId);
        var typeIds = _implantRepository is null
            ? (IReadOnlyList<int>)[]
            : await _implantRepository.GetTypeIdsAsync(characterId);

        _activeImplants = ImplantSource.FromCharacter(typeIds);
        ImplantStatus = typeIds.Count == 0
            ? result?.Message ?? $"{mode.Label}: no implants"
            : result is { IsSuccess: true } ? $"{mode.Label}: {typeIds.Count} implants" : $"{mode.Label}: cached {typeIds.Count}";
        await PopulateImplantsAsync(typeIds);
        await RecomputeAsync();
    }

    // Restore the persisted implant source on open — cache-only so opening a fit never fires a fresh ESI import.
    private async Task ApplyRememberedImplantModeAsync()
    {
        var mode = ResolveRememberedImplantMode();
        if (mode is null)
            return;
        SetSelectedImplantMode(mode);

        if (mode.CharacterId is not { } characterId)
        {
            _activeImplants = ImplantSource.FromFit;
            ImplantStatus = "Fit implants";
            return;
        }

        var typeIds = _implantRepository is null
            ? (IReadOnlyList<int>)[]
            : await _implantRepository.GetTypeIdsAsync(characterId);
        _activeImplants = ImplantSource.FromCharacter(typeIds);
        ImplantStatus = $"{mode.Label}: {typeIds.Count} implants (cached)";
        await PopulateImplantsAsync(typeIds);
    }

    private ImplantModeViewModel? ResolveRememberedImplantMode()
    {
        if (string.IsNullOrEmpty(_rememberedImplantMode))
            return null;
        if (_rememberedImplantMode == "fit")
            return ImplantModes.FirstOrDefault(mode => mode.CharacterId is null);
        var parts = _rememberedImplantMode.Split(':', 2);
        return parts is ["char", var idText] && int.TryParse(idText, out var id)
            ? ImplantModes.FirstOrDefault(mode => mode.CharacterId == id)
            : null;
    }

    // Fill the IMPLANTS panel with the applied implants (icon + name), resolving names from the SDE.
    private async Task PopulateImplantsAsync(IReadOnlyList<int> typeIds)
    {
        Implants.Clear();
        foreach (var typeId in typeIds)
        {
            var name = _sde is not null && _sde.TryGetTypeName(typeId, out var resolved) ? resolved : $"Type {typeId}";
            Implants.Add(new ImplantSlotViewModel(typeId, name, _images));
        }
        OnPropertyChanged(nameof(HasImplants));
        foreach (var slot in Implants)
            await slot.LoadImageAsync();
    }

    private void SetSelectedImplantMode(ImplantModeViewModel mode)
    {
        _suppressImplantApply = true;
        SelectedImplantMode = mode;
        _suppressImplantApply = false;
    }

    private static string EncodeImplantMode(ImplantModeViewModel mode) =>
        mode.CharacterId is { } characterId ? $"char:{characterId}" : "fit";

    // The "+ add booster" menu: every SDE booster type, each adding itself as an active simulated booster.
    private IReadOnlyList<ChargeMenuOptionViewModel> BuildBoosterMenu(ISdeAccessor? sde) =>
        sde is null
            ? []
            : sde.GetBoosterTypes()
                .Select(booster => new ChargeMenuOptionViewModel(booster.Name,
                    new RelayCommand(() => _ = AddBoosterAsync(booster.TypeId, booster.Name))))
                .ToList();

    private IReadOnlyList<TacticalModeViewModel> BuildTacticalModes(IDogmaDataAccessor? data, int shipTypeId)
    {
        var modes = data?.GetTacticalModes(shipTypeId) ?? [];
        if (modes.Count == 0)
            return [];

        _selectedModeTypeId = modes[0].TypeId;   // Defense (lowest type id) is the engine's default stance
        return modes
            .Select(mode => new TacticalModeViewModel(mode.TypeId, ModeLabel(mode.Name, ShipName),
                new RelayCommand(() => _ = SelectModeAsync(mode.TypeId)))
            { IsSelected = mode.TypeId == _selectedModeTypeId })
            .ToList();
    }

    // "Confessor Defense Mode" -> "Defense"; falls back to the full name if stripping leaves nothing.
    private static string ModeLabel(string modeName, string shipName)
    {
        var label = modeName.Replace(shipName, "").Replace("Mode", "").Trim();
        return string.IsNullOrWhiteSpace(label) ? modeName : label;
    }

    private async Task SelectModeAsync(int modeTypeId)
    {
        _selectedModeTypeId = modeTypeId;
        foreach (var mode in TacticalModes)
            mode.IsSelected = mode.TypeId == modeTypeId;
        await RecomputeAsync();
    }

    /// <summary>Computes the initial stats (each module at its default state) and the fit's ISK value. Call once after constructing.</summary>
    public async Task InitializeAsync()
    {
        await ApplyRememberedSkillModeAsync();   // restore the last-used skill mode (cache-only, no fresh import)
        await ApplyRememberedImplantModeAsync(); // restore the last-used implant source (cache-only)
        await RecomputeAsync();
        await LoadValueAsync();
    }

    /// <summary>Estimates the fit's ISK value from the cached ESI average prices (hull + every item × quantity);
    /// leaves the placeholder when the price cache has not been populated yet.</summary>
    public async Task LoadValueAsync()
    {
        if (_prices is null)
            return;
        var typeIds = _fit.Items.Select(item => item.TypeId).Append(_fit.ShipTypeId).Distinct().ToList();
        var averages = await _prices.GetAveragePricesAsync(typeIds);
        if (averages.Count == 0)
            return;   // cache empty -> keep the placeholder

        var total = averages.GetValueOrDefault(_fit.ShipTypeId);
        foreach (var item in _fit.Items)
            total += averages.GetValueOrDefault(item.TypeId) * item.Quantity;
        IskValue = IskFormat.Short(total);
    }

    private async Task RecomputeAsync()
    {
        var activeDrones = DroneBay
            .Where(drone => drone.ActiveQuantity > 0)
            .Select(drone => new DroneInput(drone.TypeId, drone.ActiveQuantity))
            .ToList();
        // The engine takes implants + boosters as one char-anchored implant list: the active simulated boosters plus
        // the selected source's implants (a character's actual implants; empty for the fit's own).
        var implantInputs = Boosters
            .Where(booster => booster.IsActive)
            .Select(booster => new ImplantInput(booster.TypeId))
            .ToList();
        implantInputs.AddRange(_activeImplants.CharacterTypeIds.Select(typeId => new ImplantInput(typeId)));
        var stats = _provider is null
            ? null
            : await _provider.ComputeAsync(_fit, RadialSlots.Select(slot => slot.ToInput()).ToList(),
                _selectedModeTypeId, activeDrones, implantInputs, _activeSkills,
                _damageProfileSelector?.CurrentProfile, _weatherSelector?.CurrentWeather, FighterBay?.LaunchedFighters);
        _stats = stats;
        // hand each slot its own resolved contribution so its tooltip shows the per-module readout. The module
        // contributions are index-aligned with the slot list (fit order); the drones follow them.
        if (_stats is not null)
        {
            for (var i = 0; i < RadialSlots.Count && i < _stats.ModuleContributions.Count; i++)
                RadialSlots[i].SetContribution(_stats.ModuleContributions[i]);
            // Hand each drone stack its own (per-drone) contribution so its bay tooltip shows the in-game readout.
            foreach (var stack in DroneBay)
                stack.SetContribution(_stats.ModuleContributions.FirstOrDefault(c => c.IsDrone && c.TypeId == stack.TypeId));
            // Hand each fighter squadron its type's per-fighter readout for the tube tooltip.
            FighterBay?.ApplyContributions(_stats.FighterContributions ?? []);
        }
        // validate against the computed budgets + the character's trained skills (null skills = all-V, no gaps).
        _validation = _validator is null || _stats is null
            ? FitValidationResult.Empty
            : _validator.Validate(_fit, _stats, _trainedSkills);
        OnPropertyChanged(string.Empty);   // every formatted stat reads _stats, so refresh all bindings at once
    }

    // ── Fit-validation surface: the "Skills Required" panel, the fitting-alert banner and the header badges ──

    /// <summary>Resource budgets the fit blows (CPU / PG / Calibration / drone bay / bandwidth) — the in-game fitting alert.</summary>
    public bool HasFittingAlerts => _validation.Overloads.Count > 0;
    public IReadOnlyList<string> FittingAlerts => _validation.Overloads
        .Select(overload => $"{ResourceLabel(overload.Resource)} overloaded").ToList();

    private bool _matchInGameRate;

    /// <summary>Toggle: estimate at EVE's in-game fitting-panel baseline (~25 SP/min, generic) instead of the
    /// character's real attributes + implants, so the SP/time line up 1:1 with the in-game "Skills Required" panel
    /// . Off (default) = the character's own attributes, which is what the real skill queue uses and the
    /// accurate figure for that pilot.</summary>
    public bool MatchInGameRate
    {
        get => _matchInGameRate;
        set
        {
            if (!SetProperty(ref _matchInGameRate, value))
                return;
            OnPropertyChanged(nameof(SkillGaps));
            OnPropertyChanged(nameof(HasSkillTrainingTotal));
            OnPropertyChanged(nameof(SkillTrainingTotal));
            OnPropertyChanged(nameof(HasEffectiveAttributes));
            OnPropertyChanged(nameof(EffectiveAttributesLabel));
        }
    }

    // The attribute set the SP/time estimate runs on: the in-game generic baseline when the toggle is on, otherwise the
    // character's effective attributes (base + implants). Null only when neither applies (no character / not imported).
    private CharacterAttributeSet? _RateAttributes =>
        _matchInGameRate ? CharacterAttributeSet.FittingPanelBaseline : _effectiveAttributes;

    /// <summary>Skills the selected character still needs to fly the fit (empty on the all-V baseline). Info only — no buy/train.</summary>
    public bool HasSkillGaps => _validation.SkillGaps.Count > 0;
    public IReadOnlyList<SkillGapViewModel> SkillGaps => _validation.SkillGaps
        .Select(gap => new SkillGapViewModel(_names.TypeName(gap.SkillTypeId), gap.RequiredLevel, gap.CurrentLevel,
            // SP + Omega time at the chosen rate (the character's real attributes + implants, or the in-game baseline
            // when MatchInGameRate is on); null on the all-V baseline or when attributes aren't imported.
            _trainingEstimator is not null && _RateAttributes is { } rate
                ? _trainingEstimator.Estimate(gap.SkillTypeId, gap.CurrentLevel, gap.RequiredLevel, rate)
                : null))
        .ToList();

    // Collapsed by default to the first few skills, with a toggle to show all (the list can be long on a fresh character).
    private const int CollapsedSkillCount = 3;
    private bool _showAllSkills;
    public bool ShowAllSkills
    {
        get => _showAllSkills;
        set
        {
            if (!SetProperty(ref _showAllSkills, value)) return;
            OnPropertyChanged(nameof(VisibleSkillGaps));
            OnPropertyChanged(nameof(SkillsToggleLabel));
        }
    }
    public bool CanToggleSkills => _validation.SkillGaps.Count > CollapsedSkillCount;
    public IReadOnlyList<SkillGapViewModel> VisibleSkillGaps =>
        _showAllSkills ? SkillGaps : SkillGaps.Take(CollapsedSkillCount).ToList();
    public string SkillsToggleLabel =>
        _showAllSkills ? "Show less" : $"Show all {_validation.SkillGaps.Count}";
    private ICommand? _toggleSkillsCommand;
    public ICommand ToggleSkillsCommand => _toggleSkillsCommand ??= new RelayCommand(() => ShowAllSkills = !ShowAllSkills);

    /// <summary>The grand total to train every missing skill — total SP + summed Omega time (in-game's panel footer);
    /// empty unless the character's attributes are known and there is at least one gap.</summary>
    public bool HasSkillTrainingTotal => _SkillTrainingTotal is not null;
    public string SkillTrainingTotal =>
        _SkillTrainingTotal is { } total ? SkillEstimateFormat.SpAndTime(total.SkillPoints, total.Time) : "";

    /// <summary>A note shown only when the in-game generic baseline is in effect, to explain that the estimate ignores the
    /// character's own attributes. The character's raw attribute values are not surfaced — they read like debug values
    /// the line is hidden when the character's real rate is used.</summary>
    public bool HasEffectiveAttributes => _matchInGameRate;
    public string EffectiveAttributesLabel => _matchInGameRate
        ? "In-game fitting estimate · ~25 SP/min (generic baseline, not your attributes)"
        : "";

    private (double SkillPoints, TimeSpan Time)? _SkillTrainingTotal
    {
        get
        {
            if (_trainingEstimator is null || _RateAttributes is not { } rate || _validation.SkillGaps.Count == 0)
                return null;
            var skillPoints = 0.0;
            var time = TimeSpan.Zero;
            foreach (var gap in _validation.SkillGaps)
            {
                var estimate = _trainingEstimator.Estimate(gap.SkillTypeId, gap.CurrentLevel, gap.RequiredLevel, rate);
                skillPoints += estimate.SkillPointsRequired;
                time += estimate.TrainingTime;
            }
            return (skillPoints, time);
        }
    }

    // Header badges (in-game warning / info counts): resource overloads read as warnings, missing skills as info.
    public int WarningCount => _validation.Overloads.Count;
    public int InfoCount => _validation.SkillGaps.Count;
    public bool HasWarnings => WarningCount > 0;
    public bool HasInfo => InfoCount > 0;

    private static string ResourceLabel(FitResource resource) => resource switch
    {
        FitResource.Cpu => "CPU",
        FitResource.PowerGrid => "Power Grid",
        FitResource.Calibration => "Calibration",
        FitResource.DroneBay => "Drone Bay",
        FitResource.DroneBandwidth => "Drone Bandwidth",
        _ => resource.ToString()
    };

    // ── Drone deploy/recall (the in-game "drones in space" selection) ──

    private static double BaseAttribute(IDogmaDataAccessor? data, int typeId, int attributeId) =>
        data?.GetBaseAttributes(typeId).FirstOrDefault(attribute => attribute.AttributeId == attributeId)?.Value ?? 0;

    // The STORAGE panel rows: the cargo hold (which lives in the Type.capacity column, not a dogma attribute), then each
    // special hold the hull carries (dogma attributes), then a structure's fuel bay (not in the SDE — hard-coded). Only
    // bays with a positive capacity are listed, so a hull with no special holds simply hides the panel.
    // The Fighter Bay panel for a carrier/supercarrier/Upwell structure: built when the hull carries launch tubes (attr
    // 2216). Each fitted fighter is one squadron; a FighterTube* flag starts it launched, a FighterBay* flag in reserve.
    // The fighter accessor derives each squadron's kind/size/volume from the SDE. The panel is a simulation overlay — it
    // never mutates the stored fit — so it stays visible (with empty tubes) even when the fit carries no fighters.
    private FighterBayViewModel? BuildFighterBay(EsiFitting fit, FighterAccessor? fighters, IDogmaDataAccessor? data)
    {
        if (fighters is null)
            return null;
        var tubes = (int)BaseAttribute(data, fit.ShipTypeId, FighterTubesAttr);
        if (tubes <= 0)
            return null;

        var bay = new FighterBayViewModel(tubes,
            (int)BaseAttribute(data, fit.ShipTypeId, FighterLightSlotsAttr),
            (int)BaseAttribute(data, fit.ShipTypeId, FighterSupportSlotsAttr),
            (int)BaseAttribute(data, fit.ShipTypeId, FighterHeavySlotsAttr),
            BaseAttribute(data, fit.ShipTypeId, FighterBayCapacityAttr),
            RecomputeAsync);

        // A fighter is any category-87 type, wherever the import put it (launch tubes, fighter bay, or — as EFT/ESI often
        // do for a carrier — the cargo hold). The item Quantity counts individual fighters, so a type's fighters group
        // into ceil(total / squadron size) squadrons. They auto-load into the tubes up to the per-kind limits (the
        // in-game "simulated" default), the rest waiting in the bay as reserves.
        var fighterFleet = fit.Items
            .Select(item => (Type: fighters.GetFighterType(item.TypeId), item.Quantity))
            .Where(entry => entry.Type is not null)
            .GroupBy(entry => entry.Type!.TypeId)
            .Select(group => (Type: group.First().Type!, Fighters: group.Sum(entry => entry.Quantity)));
        foreach (var (type, fighterCount) in fighterFleet)
        {
            var squadrons = type.SquadronMaxSize > 0
                ? (int)Math.Ceiling((double)fighterCount / type.SquadronMaxSize)
                : fighterCount;
            for (var squadron = 0; squadron < squadrons; squadron++)
                bay.Seed(new FighterSquadronViewModel(type, _images), launched: true);
        }
        return bay;
    }

    private IReadOnlyList<StorageBayViewModel> BuildStorageBays(IDogmaDataAccessor? data)
    {
        var bays = new List<StorageBayViewModel>();
        var cargo = data?.GetCapacity(_fit.ShipTypeId) ?? 0;
        if (cargo > 0)
            bays.Add(new StorageBayViewModel("Cargo Hold", FormatVolume(cargo)));
        foreach (var (attributeId, name) in StorageBayDefinitions.All)
        {
            var volume = BaseAttribute(data, _fit.ShipTypeId, attributeId);
            if (volume > 0)
                bays.Add(new StorageBayViewModel(name, FormatVolume(volume)));
        }
        // An Upwell structure keeps its fuel bay outside dogma (category-65 hulls carry no bay attributes); surface the
        // hard-coded StructureFuelBay so the panel reflects that a structure has storage too.
        if (_isStructure)
            bays.Add(new StorageBayViewModel("Fuel Bay", FormatVolume(StructureFuelBay.FuelBayCapacityM3)));
        return bays;
    }

    private static string FormatVolume(double cubicMeters) =>
        $"{cubicMeters.ToString("#,##0", CultureInfo.InvariantCulture)} m³";

    private int TotalActiveDrones => DroneBay.Sum(drone => drone.ActiveQuantity);
    private double ActiveBandwidthUsed => DroneBay.Sum(drone => drone.ActiveQuantity * drone.BandwidthPerDrone);

    private bool CanDeploy(DroneBayItemViewModel stack)
    {
        if (stack.ActiveQuantity >= stack.BayQuantity) return false;              // none left in the bay
        if (TotalActiveDrones >= MaxActiveDrones) return false;                   // universal 5-drone limit
        return _shipDroneBandwidth <= 0                                           // unknown bandwidth -> only the 5-cap
            || ActiveBandwidthUsed + stack.BandwidthPerDrone <= _shipDroneBandwidth + 0.01;
    }

    private void DeployDefaultDrones()
    {
        foreach (var stack in DroneBay)
            while (CanDeploy(stack))
                stack.ActiveQuantity++;
    }

    // Set how many drones of this stack are in space (the in-game "Selected:" checkbox row). Deploys up to the requested
    // count while the universal 5-drone limit and the ship's bandwidth allow it, or recalls down to it; the stack mirrors
    // the clamped result back onto its checkboxes.
    private async Task SetDroneActiveAsync(DroneBayItemViewModel stack, int desiredActive)
    {
        var target = Math.Clamp(desiredActive, 0, stack.BayQuantity);
        if (target == stack.ActiveQuantity)
            return;

        if (target > stack.ActiveQuantity)
            while (stack.ActiveQuantity < target && CanDeploy(stack))
                stack.ActiveQuantity++;
        else
            while (stack.ActiveQuantity > target)
                stack.ActiveQuantity--;

        OnPropertyChanged(nameof(DroneActiveLabel));
        await RecomputeAsync();
    }

    // ── Booster simulation (a what-if overlay; not stored in the fit) ──

    private async Task AddBoosterAsync(int typeId, string name)
    {
        if (Boosters.Any(booster => booster.TypeId == typeId))
            return;   // already simulated
        var booster = new BoosterViewModel(typeId, name, isActive: true, _images, RecomputeAsync, RemoveBoosterAsync);
        Boosters.Add(booster);
        OnPropertyChanged(nameof(HasBoosters));
        await booster.LoadImageAsync();
        await RecomputeAsync();
    }

    private async Task RemoveBoosterAsync(BoosterViewModel booster)
    {
        Boosters.Remove(booster);
        OnPropertyChanged(nameof(HasBoosters));
        await RecomputeAsync();
    }

    /// <summary>Best-effort: when the user has opted in, pull the ship render and each slot's icon from the CCP
    /// image server (cached). Fire-and-forget after the window is shown so the images pop in without blocking it; a
    /// failure leaves the offline glyphs/silhouette in place.</summary>
    public async Task LoadImagesAsync()
    {
        if (_images is null || !await _images.AreImagesEnabledAsync())
            return;
        try
        {
            ShipImage = await _images.GetImageAsync(_fit.ShipTypeId, TypeImageKind.Render, 512);
            foreach (var slot in RadialSlots)
                await slot.LoadImageAsync();
            foreach (var charge in AvailableCharges)
                await charge.LoadImageAsync();
            foreach (var group in ChargeModuleGroups)
                await group.LoadImageAsync();
            foreach (var drone in DroneBay)
                await drone.LoadImageAsync();
            if (FighterBay is { } fighterBay)
                foreach (var squadron in fighterBay.Tubes.Where(t => t.Squadron is not null).Select(t => t.Squadron!).Concat(fighterBay.Reserves))
                    await squadron.LoadImageAsync();
            foreach (var booster in Boosters)
                await booster.LoadImageAsync();
            foreach (var cargo in CargoItems)
                await cargo.LoadImageAsync();
        }
        catch
        {
            // image loading is best-effort; the glyphs remain
        }
    }

    // Whether `slot` may move to an active state given the fit's other modules; shows the reason and returns false when a
    // cross-module rule (e.g. cloak mutual exclusion) refuses it.
    private bool _CanActivate(ModuleSlotViewModel slot, IDogmaDataAccessor data)
    {
        var others = RadialSlots.Where(other => !ReferenceEquals(other, slot)).Select(other => other.ToInput()).ToList();
        if (ModuleActivationRules.FirstConflict(slot.TypeId, others, data) is not { } conflict)
            return true;
        _toasts?.Show("Activation blocked",
            $"You can't activate {slot.Name} because {_names.TypeName(conflict.BlockingTypeId)} is active.", ToastKind.Warning);
        return false;
    }

    private (List<ModuleSlotViewModel> Filled, List<FitRadialSlotViewModel> Empty, List<DraggableChargeViewModel> Charges) BuildSlots(
        EsiFitting fit, ISdeNameResolver names, ISdeAccessor? sde, IDogmaDataAccessor? data)
    {
        // First pass in fit order so the max-active group clamp keeps the first propulsion module active.
        var accumulator = new ModuleStateAccumulator();
        var seenFlags = new HashSet<string>();
        var descriptors = new List<SlotDescriptor>();
        foreach (var entry in fit.Items)
        {
            var category = FitSlotClassifier.Classify(entry.Flag);
            if (category is not (FitSlotCategory.High or FitSlotCategory.Medium or FitSlotCategory.Low
                or FitSlotCategory.Rig or FitSlotCategory.Subsystem or FitSlotCategory.Service)) continue;
            if (!seenFlags.Add(entry.Flag)) continue;

            var slot = fit.Items.Where(item => item.Flag == entry.Flag).ToList();
            var module = sde is null ? slot[0] : slot.FirstOrDefault(item => sde.GetSlotType(item.TypeId) != SdeSlotType.None);
            if (module is null) continue;
            var charge = sde is null ? null : slot.FirstOrDefault(item => sde.GetSlotType(item.TypeId) == SdeSlotType.None);

            var state = data is null ? ModuleState.Online : ModuleStateResolver.DefaultState(module.TypeId, data, accumulator);
            var valid = data is null ? [ModuleState.Online] : ModuleStateResolver.ValidStates(module.TypeId, data).ToArray();
            descriptors.Add(new SlotDescriptor(entry.Flag, module.TypeId, charge?.TypeId, category, state, valid));
        }

        // The hull's slot counts (Dogma base attrs) let us draw the full ring including empty placeholders, like
        // eveship.fit. When the counts are unknown (no engine data) we fall back to just the filled slots.
        var baseAttributes = data?.GetBaseAttributes(fit.ShipTypeId) ?? [];
        double? HullAttribute(int attributeId) =>
            baseAttributes.FirstOrDefault(attribute => attribute.AttributeId == attributeId)?.Value;

        var filled = new List<ModuleSlotViewModel>();
        var empty = new List<FitRadialSlotViewModel>();
        var chargeCatalog = new Dictionary<int, SdeChargeType>();   // union of compatible charges across the fit (2f)

        // Cross-module activation rules: evaluated against the live slot states each time a module is cycled to
        // active, so it sees the fit's current states. No engine data -> no guard (activation always allowed).
        var activationGuard = data is null
            ? (Func<ModuleSlotViewModel, ModuleState, bool>?)null
            : (slot, _) => _CanActivate(slot, data);

        // Each slot category sits on a fixed arc — a fixed start angle + a fixed ~10° step per slot — matching
        // eveship.fit and the in-game wheel: a ship fills each arc from its start, leaving the rest of the arc empty.
        foreach (var category in SlotOrder)
        {
            var byIndex = descriptors
                .Where(descriptor => descriptor.Category == category)
                .ToDictionary(descriptor => FitSlotClassifier.SlotIndex(descriptor.Flag));
            var hullCount = SlotCountAttribute.TryGetValue(category, out var attributeId) ? (int?)HullAttribute(attributeId) : null;
            var filledMax = byIndex.Count > 0 ? byIndex.Keys.Max() + 1 : 0;
            // Lay out the FULL category arc so the ring is uniformly full (in-game behaviour): up to the
            // universal max per category (8 high/mid/low, 4 subsystem, 3 rig) — the hull's slots filled/empty, the rest as
            // placeholders. Structures (service slots) have no fixed cap, so they use the hull count. Never fewer than filled.
            var max = MaxSlots.TryGetValue(category, out var categoryMax) ? categoryMax : (hullCount ?? 0);
            var count = Math.Max(max, filledMax);
            if (count == 0) continue;

            var (startDegrees, stepDegrees) = ArcLayout.GetValueOrDefault(category, (180.0, 10.0));
            for (var i = 0; i < count; i++)
            {
                var angle = startDegrees + stepDegrees * i;
                var shape = SlotShape(angle, stepDegrees);
                if (byIndex.TryGetValue(i, out var descriptor))
                {
                    var (iconLeft, iconTop) = IconPos(angle);
                    var chargeOptions = sde is null ? (IReadOnlyList<SdeChargeType>)[] : ChargeCompatibility.For(descriptor.TypeId, sde);
                    foreach (var option in chargeOptions) chargeCatalog.TryAdd(option.TypeId, option);
                    filled.Add(new ModuleSlotViewModel(descriptor.TypeId, descriptor.ChargeTypeId, descriptor.Category,
                        names.TypeName(descriptor.TypeId), shape, iconLeft, iconTop, Glyphs.GetValueOrDefault(descriptor.Category, "·"),
                        descriptor.State, descriptor.ValidStates, chargeOptions, _images, RecomputeAsync, _onShowInfo, activationGuard,
                        slotNumber: i + 1));
                }
                else
                {
                    empty.Add(new FitRadialSlotViewModel(shape, "", "#3A4A56", $"Empty {category.ToString().ToLowerInvariant()} slot"));
                }
            }
        }
        var charges = chargeCatalog.Values.OrderBy(option => option.Name)
            .Select(option => new DraggableChargeViewModel(option.TypeId, option.Name, _images))
            .ToList();
        return (filled, empty, charges);
    }

    // The curved annular-segment tile centred on angleDeg, spanning the slot's angular step minus a small gap. Its top
    // edge follows the outer ring radius, its bottom the inner one, the sides are radial — the EVE / eveship slot
    // shape (our own geometry). 0° = top, clockwise positive. Invariant formatting so the
    // path parses on any OS locale.
    private static string SlotShape(double angleDeg, double stepDeg)
    {
        var half = Math.Max(stepDeg - SlotGapDeg, 1.0) / 2.0;
        var a0 = (angleDeg - half) * Math.PI / 180.0;
        var a1 = (angleDeg + half) * Math.PI / 180.0;
        var ox0 = Center + SlotOuter * Math.Sin(a0); var oy0 = Center - SlotOuter * Math.Cos(a0);
        var ox1 = Center + SlotOuter * Math.Sin(a1); var oy1 = Center - SlotOuter * Math.Cos(a1);
        var ix1 = Center + SlotInner * Math.Sin(a1); var iy1 = Center - SlotInner * Math.Cos(a1);
        var ix0 = Center + SlotInner * Math.Sin(a0); var iy0 = Center - SlotInner * Math.Cos(a0);
        return FormattableString.Invariant(
            $"M {ox0:0.##},{oy0:0.##} A {SlotOuter},{SlotOuter} 0 0 1 {ox1:0.##},{oy1:0.##} L {ix1:0.##},{iy1:0.##} A {SlotInner},{SlotInner} 0 0 0 {ix0:0.##},{iy0:0.##} Z");
    }

    // Top-left of the upright module icon, centred at the segment's mid-radius. 0° = top, clockwise positive.
    private static (double Left, double Top) IconPos(double angleDeg)
    {
        var mid = (SlotInner + SlotOuter) / 2.0;
        var radians = angleDeg * Math.PI / 180.0;
        return (Center + mid * Math.Sin(radians) - IconSize / 2, Center - mid * Math.Cos(radians) - IconSize / 2);
    }

    private sealed record SlotDescriptor(string Flag, int TypeId, int? ChargeTypeId, FitSlotCategory Category,
        ModuleState State, ModuleState[] ValidStates);

    private sealed record FuelServiceModule(ModuleSlotViewModel Slot, double FuelPerHour, double OnlineCost);

    // ── Stats (read off the latest FitStats; "—" until computed / when the SDE is unavailable) ──

    public bool HasStats => _stats is not null;

    /// <summary>True for an Upwell structure (category 65): the radial carries service slots + a fuel panel, and the
    /// navigation panel is hidden (a structure does not move).</summary>
    public bool IsStructure => _isStructure;

    /// <summary>The navigation panel (velocity / align / warp) is meaningless for an immobile structure.</summary>
    public bool HasNavigationPanel => !_isStructure;

    // ── Structure fuel (D): online service modules draw fuel blocks per hour; the bay capacity gives a runtime estimate ──

    public bool HasFuel => _serviceModules.Count > 0;

    /// <summary>One row per service module: name + hourly fuel draw (or "offline"), reflecting its current online state.
    /// Re-read on every recompute, so offlining a service in the wheel drops its draw here live.</summary>
    public IReadOnlyList<FuelRowViewModel> FuelRows => _serviceModules
        .Select(module => new FuelRowViewModel(module.Slot.Name, module.Slot.State >= ModuleState.Online,
            module.FuelPerHour, module.OnlineCost))
        .ToList();

    private double FuelPerHour => _serviceModules
        .Where(module => module.Slot.State >= ModuleState.Online)
        .Sum(module => module.FuelPerHour);

    public string FuelConsumption => HasFuel ? $"{FuelPerHour:0} blocks/h" : "—";

    public string FuelRuntime
    {
        get
        {
            var perHour = FuelPerHour;
            if (_fuelBayCapacity <= 0) return "bay capacity unverified";   // not yet entered from in-game (not in the SDE)
            if (perHour <= 0) return "no online services";
            return $"{_fuelBayCapacity / perHour / 24.0:0.0} days on a full bay";
        }
    }

    /// <summary>The capacitor ring colour: cyan when stable, amber when it depletes, dim when unknown (the in-game cap ring).</summary>
    public IBrush CapacitorRingBrush => new SolidColorBrush(Color.Parse(
        _stats is null ? "#3A4650" : _stats.CapacitorStable ? "#4EC8D9" : "#D9A441"));

    // ── CPU / PowerGrid / Calibration arc-gauges on the outer rim ──
    // Fixed start angle + sweep per gauge (0° = top, clockwise positive); the fill arc covers used/total of the sweep.
    public IReadOnlyList<RingGaugeViewModel> RingGauges
    {
        get
        {
            if (_stats is null) return [];
            var gauges = new List<RingGaugeViewModel>
            {
                Gauge(134.5, -44, 40, 5, _stats.CpuUsed, _stats.CpuOutput, "#7BACC3", "#BFE0E6", "CPU"),         // CPU — blue
                Gauge(135, 44, 40, 5, _stats.PowerUsed, _stats.PowerOutput, "#C13B2D", "#F08070", "Power Grid"), // Power Grid — red
            };
            if (_calibrationTotal > 0)
                gauges.Add(Gauge(-47, -30, 30, 2, _calibrationUsed, _calibrationTotal, "#AEB9C2", "#DCE4E9",     // Calibration (rigs) — white/grey
                                 "Calibration", "Fitting resource for rigs"));
            return gauges;
        }
    }

    public string Calibration => _calibrationTotal > 0 ? $"{_calibrationUsed:0} / {_calibrationTotal:0}" : "—";

    // ── Turret / launcher hardpoint indicator dots (in-game wheel; validated against an in-game reference) ──
    // Dots sit on the rim just above the high-slot tiles. Turret: the first dot's LEFT edge is on the LEFT edge of the FIRST
    // high slot, then dots step clockwise (toward 12 o'clock), close together. Launcher: the first dot's RIGHT edge is on the
    // RIGHT edge of the LAST high slot, stepping anti-clockwise (toward 12 o'clock). Used dots (a turret/launcher fitted) come first.
    public IReadOnlyList<HardpointPipViewModel> TurretHardpoints =>   // first dot's left edge on the first slot's left edge
        HardpointPips(_turretHardpointsUsed, _turretHardpointsTotal, FirstHighSlotLeftEdge + HardpointDotAngularRadius, +1, "Turret");

    public IReadOnlyList<HardpointPipViewModel> LauncherHardpoints =>  // first dot's right edge on the last slot's right edge
        HardpointPips(_launcherHardpointsUsed, _launcherHardpointsTotal, LastHighSlotRightEdge - HardpointDotAngularRadius, -1, "Launcher");

    // Turret/launcher label glyphs, just outside each cluster's first dot (the in-game hardpoint-type labels). Shown when
    // the hull has that hardpoint type; the dots stay put.
    public bool HasTurretHardpoints => _turretHardpointsTotal > 0;
    public bool HasLauncherHardpoints => _launcherHardpointsTotal > 0;
    public double TurretIconLeft => IconLeft(TurretIconAngle);
    public double TurretIconTop => IconTop(TurretIconAngle);
    public double LauncherIconLeft => IconLeft(LauncherIconAngle);
    public double LauncherIconTop => IconTop(LauncherIconAngle);
    private static double TurretIconAngle => FirstHighSlotLeftEdge - HardpointIconGapDeg - HardpointIconAngularRadius;
    private static double LauncherIconAngle => LastHighSlotRightEdge + HardpointIconGapDeg + HardpointIconAngularRadius;

    // The outer edges of the high-slot arc. The wheel always lays out the FULL arc (MaxSlots = 8, rest as empty
    // placeholders), so the launcher side anchors to the LAST DRAWN position — not the hull's actual high count, which
    // would drift the cluster toward 12 o'clock on ships with fewer high slots.
    private static double FirstHighSlotLeftEdge =>
        ArcLayout[FitSlotCategory.High].Start - (ArcLayout[FitSlotCategory.High].Step - SlotGapDeg) / 2;
    private static double LastHighSlotRightEdge
    {
        get
        {
            var (start, step) = ArcLayout[FitSlotCategory.High];
            var drawnHigh = MaxSlots.TryGetValue(FitSlotCategory.High, out var maxHigh) ? maxHigh : 8;
            return start + step * (drawnHigh - 1) + (step - SlotGapDeg) / 2;
        }
    }

    private static double IconLeft(double deg) => Center + HardpointRadius * Math.Sin(deg * Math.PI / 180.0) - HardpointIconSize / 2;
    private static double IconTop(double deg) => Center - HardpointRadius * Math.Cos(deg * Math.PI / 180.0) - HardpointIconSize / 2;

    private static IReadOnlyList<HardpointPipViewModel> HardpointPips(int used, int total, double anchorDeg, int direction, string name)
    {
        if (total <= 0) return [];
        var tooltip = $"{name} hardpoints: {used} / {total}";
        var pips = new List<HardpointPipViewModel>(total);
        for (var i = 0; i < total; i++)
        {
            var deg = anchorDeg + direction * i * HardpointDotSpacingDeg;   // first dot on the anchor edge, then step inward
            var rad = deg * Math.PI / 180.0;
            var left = Center + HardpointRadius * Math.Sin(rad) - HardpointPipSize / 2;
            var top = Center - HardpointRadius * Math.Cos(rad) - HardpointPipSize / 2;
            pips.Add(new HardpointPipViewModel(left, top, HardpointPipSize, i < used, tooltip));
        }
        return pips;
    }

    // The hull's turret/launcher hardpoint totals (Dogma base attrs) and how many are used (fitted turret/launcher modules,
    // classified by the pre-computed SDE fit requirement from effects 42/40). Total is never less than used.
    private static (int TurretUsed, int TurretTotal, int LauncherUsed, int LauncherTotal) Hardpoints(
        EsiFitting fit, ISdeAccessor? sde, IDogmaDataAccessor? data)
    {
        if (sde is null) return (0, 0, 0, 0);
        var baseAttributes = data?.GetBaseAttributes(fit.ShipTypeId) ?? [];
        int Total(int attributeId) =>
            (int)Math.Round(baseAttributes.FirstOrDefault(attribute => attribute.AttributeId == attributeId)?.Value ?? 0);

        int turretUsed = 0, launcherUsed = 0;
        foreach (var item in fit.Items)
        {
            // Hardpoints are consumed by fitted high-slot modules only — a turret/launcher sitting in cargo or the drone
            // bay does not occupy a hardpoint (it would otherwise wrongly light up a free hardpoint).
            if (!item.Flag.StartsWith("HiSlot", StringComparison.OrdinalIgnoreCase)) continue;
            var requirement = sde.GetFitRequirement(item.TypeId);
            if (requirement is null) continue;
            if (requirement.IsTurret) turretUsed++;
            else if (requirement.IsLauncher) launcherUsed++;
        }
        return (turretUsed, Math.Max(Total(DogmaAttributeIds.TurretHardpoints), turretUsed),
                launcherUsed, Math.Max(Total(DogmaAttributeIds.LauncherHardpoints), launcherUsed));
    }

    private static RingGaugeViewModel Gauge(double startDeg, double sweepDeg, int intervals, int markers, double used,
                                            double total, string fillHex, string coreHex, string name, string? description = null)
    {
        var fraction = total > 0 ? Math.Min(used / total, 1.0) : 0;
        var isOverBudget = total > 0 && used > total;
        // The per-resource colour stays (CPU blue / PG red / Calibration white-grey) even over budget. Recolouring the arc
        // red made CPU's blue arc merge into PG's red and vanish; instead an over-budget gauge pulses its fill and shows a
        // red readout, mirroring the in-game overload cue.
        var fill = new SolidColorBrush(Color.Parse(fillHex));
        var core = new SolidColorBrush(Color.Parse(coreHex));
        var percent = total > 0 ? used / total * 100 : 0;
        var tooltip = description is null
            ? $"{name}\n{used:0.0} / {total:0.0}  =  {percent:0.0} %"
            : $"{name}\n{description}\n{used:0.0} / {total:0.0}  =  {percent:0.0} %";
        var (onFill, offFill) = GaugeTicks(startDeg, sweepDeg, intervals, fraction);
        return new RingGaugeViewModel(Geometry.Parse(GaugeArc(startDeg, sweepDeg * fraction)),
                                      Geometry.Parse(onFill), Geometry.Parse(offFill),
                                      Geometry.Parse(GaugeMarkers(startDeg, sweepDeg, markers)),
                                      Geometry.Parse(GaugeHitArea(startDeg, sweepDeg)), fill, core, isOverBudget, tooltip);
    }

    // Angle convention: 0° = top, clockwise positive — point = (Center + r·sin, Center − r·cos). This is identical to the
    // (-90 + rotation) cos/sin convention, so reference rotation/sweep numbers carry over unchanged. Invariant formatting so the
    // generated path strings parse on any OS locale.
    private static double Px(double deg, double r) => Center + r * Math.Sin(deg * Math.PI / 180.0);
    private static double Py(double deg, double r) => Center - r * Math.Cos(deg * Math.PI / 180.0);
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // The coloured fill arc on the ring's centreline, spanning the (already fraction-scaled) sweep. Rendered three times in
    // XAML (a blurred glow, the band, the core line).
    private static string GaugeArc(double startDeg, double sweptDeg)
    {
        if (Math.Abs(sweptDeg) < 0.2) return "M 0,0";   // no (visible) usage yet — an empty, parseable path
        var large = Math.Abs(sweptDeg) > 180 ? 1 : 0;
        var sweepFlag = sweptDeg >= 0 ? 1 : 0;
        return $"M {F(Px(startDeg, GaugeRadius))},{F(Py(startDeg, GaugeRadius))} " +
               $"A {F(GaugeRadius)},{F(GaugeRadius)} 0 {large} {sweepFlag} " +
               $"{F(Px(startDeg + sweptDeg, GaugeRadius))},{F(Py(startDeg + sweptDeg, GaugeRadius))}";
    }

    // White ticks across the gauge's sweep, split into those on the filled part (drawn brighter) and the unused part
    // (drawn dimmer). Tick radii are offset from the band: inner −BAND·0.4, outer +BAND·0.55.
    private static (string OnFill, string OffFill) GaugeTicks(double startDeg, double sweepDeg, int intervals, double fraction)
    {
        const double inner = GaugeRadius - GaugeBand * 0.4, outer = GaugeRadius + GaugeBand * 0.55;
        var on = new StringBuilder();
        var off = new StringBuilder();
        for (var i = 0; i <= intervals; i++)
        {
            var f = (double)i / intervals;
            var deg = startDeg + sweepDeg * f;
            var seg = FormattableString.Invariant(
                $"M {Px(deg, inner):0.##},{Py(deg, inner):0.##} L {Px(deg, outer):0.##},{Py(deg, outer):0.##} ");
            (f <= fraction ? on : off).Append(seg);
        }
        return (on.Length > 0 ? on.ToString() : "M 0,0", off.Length > 0 ? off.ToString() : "M 0,0");
    }

    // A few larger marker ticks spread evenly across the full sweep (5 for CPU/PG, 2 for Calibration).
    private static string GaugeMarkers(double startDeg, double sweepDeg, int markers)
    {
        if (markers < 1) return "M 0,0";
        const double inner = GaugeRadius - GaugeBand * 0.5, outer = GaugeRadius + GaugeBand * 0.8;
        var sb = new StringBuilder();
        for (var i = 0; i < markers; i++)
        {
            var deg = startDeg + (markers > 1 ? sweepDeg * i / (markers - 1) : 0);
            sb.Append(FormattableString.Invariant(
                $"M {Px(deg, inner):0.##},{Py(deg, inner):0.##} L {Px(deg, outer):0.##},{Py(deg, outer):0.##} "));
        }
        return sb.ToString();
    }

    // A filled annular sector spanning the gauge's full sweep — an invisible, wide hover target for the tooltip (the thin
    // ticks are nearly impossible to hover).
    private static string GaugeHitArea(double startDeg, double sweepDeg)
    {
        if (Math.Abs(sweepDeg) < 0.4) return "M 0,0";
        const double inner = GaugeRadius - GaugeBand, outer = GaugeRadius + GaugeBand;
        var large = Math.Abs(sweepDeg) > 180 ? 1 : 0;
        var sweepOuter = sweepDeg >= 0 ? 1 : 0;   // outer arc follows the sweep direction; inner arc runs back
        var sweepInner = sweepDeg >= 0 ? 0 : 1;
        return $"M {F(Px(startDeg, outer))},{F(Py(startDeg, outer))} " +
               $"A {F(outer)},{F(outer)} 0 {large} {sweepOuter} {F(Px(startDeg + sweepDeg, outer))},{F(Py(startDeg + sweepDeg, outer))} " +
               $"L {F(Px(startDeg + sweepDeg, inner))},{F(Py(startDeg + sweepDeg, inner))} " +
               $"A {F(inner)},{F(inner)} 0 {large} {sweepInner} {F(Px(startDeg, inner))},{F(Py(startDeg, inner))} Z";
    }

    public string StatsNotice => _stats is not null
        ? ""
        : "Stats need the SDE — download it from Settings, then reopen this fit.";

    // Firepower
    public string TotalDps => Dps(_stats?.TotalDps);
    public string WeaponDps => Dps(_stats?.WeaponDps);
    public string DroneDps => Dps(_stats?.DroneDps);

    /// <summary>Launched fighter squadron DPS, for the Fighter Bay bar (the in-game "Fighters" readout).</summary>
    public string FighterDps => Dps(_stats?.FighterDps);
    public bool HasFighterDps => _stats?.FighterDps > 0;

    // The OFFENSE total reads as a base–max range when an entropic disintegrator is fitted (it spools its damage up over
    // its cycles); a single value for every other fit.
    public string TotalDpsLabel => _stats is { } stats && stats.TotalDpsMax > stats.TotalDps + 0.05
        ? $"{stats.TotalDps:0.0} – {Dps(stats.TotalDpsMax)}"
        : TotalDps;

    // In-game OFFENSE breakdown (tooltips/12): the turrets / missiles / drones split shown on hover; null (no tooltip)
    // when the fit deals no damage. Disintegrators read out their spooled base–max turret range.
    public string? DpsBreakdown
    {
        get
        {
            if (_stats is not { } stats) return null;
            var lines = DpsBreakdownLines(stats).ToList();
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
    }

    private static IEnumerable<string> DpsBreakdownLines(FitStats stats)
    {
        if (stats.TurretDps > 0)
        {
            var turrets = stats.TurretDpsMax > stats.TurretDps + 0.05
                ? $"Turrets: {stats.TurretDps:0.0} – {stats.TurretDpsMax:0.0} dps"
                : $"Turrets: {stats.TurretDps:0.0} dps";
            yield return WithReload(turrets, stats.TurretDps, stats.TurretDpsSustained);
        }
        if (stats.MissileDps > 0)
            yield return WithReload($"Missiles: {stats.MissileDps:0.0} dps", stats.MissileDps, stats.MissileDpsSustained);
        if (stats.DroneDps > 0)
            yield return $"Drones: {stats.DroneDps:0.0} dps";   // drones reload nothing — burst is sustained
        if (stats.FighterDps > 0)
            yield return WithReload($"Fighters: {stats.FighterDps:0.0} dps", stats.FighterDps, stats.FighterDpsSustained);
    }

    // Appends the in-game "(reload …)" sustained note when a weapon's clip+reload meaningfully lowers its long-run DPS.
    private static string WithReload(string line, double burst, double sustained) =>
        sustained > 0 && sustained < burst - 0.05 ? $"{line} (reload {sustained:0.0})" : line;

    // Resource usage (CPU/PG sit in the footer like the in-game fitting sim; drone bay/bandwidth in the drones panel)
    public string Cpu => _stats is null ? "—" : $"{_stats.CpuUsed:0.0} / {_stats.CpuOutput:0.0} tf";
    public string Power => _stats is null ? "—" : $"{_stats.PowerUsed:0.0} / {_stats.PowerOutput:0.0} MW";
    public string DroneBayVolume => _stats is null ? "—" : $"{_stats.DroneBayUsed:0} / {_stats.DroneBayAvailable:0} m³";
    public string Bandwidth => _stats is null ? "—" : $"{_stats.DroneBandwidthUsed:0} / {_stats.DroneBandwidthAvailable:0} Mbit/s";
    public string DroneCount => _stats is null ? "—" : $"{_stats.ActiveDroneCount}";

    // The ship's special storage bays (cargo, ore hold, fleet hangar, …): one row per non-zero bay in the STORAGE panel.
    // Built once in the constructor from hull-intrinsic values (cargo from the Type.capacity column, the special holds
    // from dogma attributes, a structure's fuel bay from the hard-coded value), so they do not change with module states.
    public IReadOnlyList<StorageBayViewModel> StorageBays { get; }
    public bool HasStorageBays => StorageBays.Count > 0;

    // Over-budget flags + footer readout brush — the readout turns red when the fit exceeds that resource's budget
    // (in-game cue). A direct brush binding is used (not a style class) because a locally-set Foreground outranks a
    // Style setter in Avalonia, so the red would never apply.
    public bool CpuOver => _stats is not null && _stats.CpuUsed > _stats.CpuOutput;
    public bool PowerOver => _stats is not null && _stats.PowerUsed > _stats.PowerOutput;
    public bool CalibrationOver => _calibrationTotal > 0 && _calibrationUsed > _calibrationTotal;
    public IBrush CpuBrush => ReadoutBrush(CpuOver);
    public IBrush PowerBrush => ReadoutBrush(PowerOver);
    public IBrush CalibrationBrush => ReadoutBrush(CalibrationOver);
    // Bright = TextBrightBrush (#FFF3ECE0 in Themes/EveUtils.axaml); over = the overload red.
    private static IBrush ReadoutBrush(bool over) => new SolidColorBrush(Color.Parse(over ? "#FF5A4D" : "#FFF3ECE0"));

    // Estimated fit value from the cached ESI market prices; a placeholder until the cache is populated.
    public string IskValue { get => _iskValue; private set => SetProperty(ref _iskValue, value); }

    // Resistance
    public IReadOnlyList<ResistRowViewModel> ResistRows => _stats is null
        ? []
        :
        [
            new ResistRowViewModel("Shield", _stats.ShieldResists, _stats.ShieldEhp),
            new ResistRowViewModel("Armor", _stats.ArmorResists, _stats.ArmorEhp),
            new ResistRowViewModel("Hull", _stats.StructureResists, _stats.StructureEhp),
        ];

    public string TotalEhp
    {
        get
        {
            if (_stats is null) return "—";
            var label = _damageProfileSelector?.ProfileLabel;
            if (string.IsNullOrEmpty(label) || label == "uniform")
                return $"{_stats.Ehp:N0} ehp";
            // Raw HP isn't a damage type to defend "vs" — show it as the raw buffer instead.
            if (label == "Raw HP")
                return $"{_stats.Ehp:N0} hp  ·  raw (no resists)";
            return $"{_stats.Ehp:N0} ehp  ·  vs {label}";
        }
    }

    // Capacitor
    public string CapState => _stats is null
        ? "—"
        : _stats.CapacitorStable
            ? $"Stable {_stats.CapacitorStablePercent:0.0}%"
            : $"Depletes in {EveDurationFormatter.FormatWithSeconds(TimeSpan.FromSeconds(_stats.CapacitorDepletesInSeconds))}";
    public string CapCapacity => _stats is null ? "—" : $"{_stats.CapacitorCapacity:N0} GJ";
    public string CapDelta => _stats is null ? "—" : $"Δ {_stats.CapacitorDelta:0.0} GJ/s";
    public string CapRecharge => _stats is null ? "—" : $"{_stats.CapacitorRecharge:0.0} GJ/s peak";

    // Targeting
    public string TargetRange => _stats is null ? "—" : $"{_stats.TargetingRange / 1000:0.0} km";
    public string ScanResolution => _stats is null ? "—" : $"{_stats.ScanResolution:0} mm";
    public string MaxLocked => _stats is null ? "—" : $"{_stats.MaxLockedTargets:0}";
    public string SensorStrength => _stats is null ? "—" : $"{_stats.SensorStrength:0.0}";

    // Navigation
    public string Velocity => _stats is null ? "—" : $"{_stats.MaxVelocity:0} m/s";
    public string Mass => _stats is null ? "—" : $"{_stats.Mass:N0} kg";
    public string Agility => _stats is null ? "—" : $"{_stats.Agility:0.000}x";
    public string AlignTime => _stats is null ? "—" : $"{_stats.AlignTime:0.00}s";
    public string WarpSpeed => _stats is null ? "—" : $"{_stats.WarpSpeed:0.0} AU/s";
    public string Signature => _stats is null ? "—" : $"{_stats.SignatureRadius:0} m";

    // Mining — only a fit with mining equipment shows the panel (a positive resolved yield).
    public bool HasMiningYield => _stats is { MiningYield: > 0 };
    public string MiningYield => _stats is null ? "—" : $"{_stats.MiningYield:0.0} m³/s";
    public string MiningYieldPerMinute => _stats is null ? "—" : $"{_stats.MiningYield * 60:N0} m³/min";

    // In-game-style MINING breakdown (the mining counterpart of DpsBreakdown): the mining-modules / mining-drones split
    // shown on hover over the yield, summed from the per-module contributions; null (no tooltip) when nothing mines.
    public string? MiningBreakdown
    {
        get
        {
            if (_stats is not { MiningYield: > 0 } stats) return null;
            var lines = MiningBreakdownLines(stats).ToList();
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
    }

    private static IEnumerable<string> MiningBreakdownLines(FitStats stats)
    {
        // Only active miners count toward the live total, matching the gated aggregate yield (an offlined miner mines 0).
        var modules = stats.ModuleContributions
            .Where(c => c is { Kind: ModuleContributionKind.Mining, IsDrone: false } && c.State >= ModuleState.Active)
            .Sum(c => c.MiningYieldPerSec);
        var drones = stats.ModuleContributions
            .Where(c => c is { Kind: ModuleContributionKind.Mining, IsDrone: true } && c.State >= ModuleState.Active)
            .Sum(c => c.MiningYieldPerSec);
        if (modules > 0)
            yield return $"Mining lasers: {modules:0.0} m³/s";
        if (drones > 0)
            yield return $"Mining drones: {drones:0.0} m³/s";
    }

    // Remote assistance — panel is hidden unless at least one remote module is active.
    public bool HasRemoteAssistance => _stats is { HasRemoteAssistance: true };
    public bool HasRemoteArmorRep => _stats is { RemoteArmorRepPerSec: > 0 };
    public bool HasRemoteShieldRep => _stats is { RemoteShieldRepPerSec: > 0 };
    public bool HasRemoteHullRep => _stats is { RemoteHullRepPerSec: > 0 };
    public bool HasRemoteCapTransfer => _stats is { RemoteCapPerSec: > 0 };
    public string RemoteArmorRep => _stats is null ? "—" : $"{_stats.RemoteArmorRepPerSec:0.0} HP/s";
    public string RemoteShieldRep => _stats is null ? "—" : $"{_stats.RemoteShieldRepPerSec:0.0} HP/s";
    public string RemoteHullRep => _stats is null ? "—" : $"{_stats.RemoteHullRepPerSec:0.0} HP/s";
    public string RemoteCapTransfer => _stats is null ? "—" : $"{_stats.RemoteCapPerSec:0.0} GJ/s";
    public string RemoteArmorRange => _stats is null ? "—" : $"{_stats.RemoteArmorRangeMeters / 1000:0.0} km";
    public string RemoteShieldRange => _stats is null ? "—" : $"{_stats.RemoteShieldRangeMeters / 1000:0.0} km";
    public string RemoteHullRange => _stats is null ? "—" : $"{_stats.RemoteHullRangeMeters / 1000:0.0} km";
    public string RemoteCapRange => _stats is null ? "—" : $"{_stats.RemoteCapRangeMeters / 1000:0.0} km";

    private static string Dps(double? value) => value is null ? "—" : $"{value.Value:0.0} dps";
}
