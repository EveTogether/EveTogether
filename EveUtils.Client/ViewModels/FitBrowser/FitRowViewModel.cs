using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fittings;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Market.Repositories;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One fit in the browser, drawn either as a card or as a table row — the two densities read the same row, so a fit
/// carries one set of figures however it is shown. Uniform across the Local and server tabs: a Local fit and a
/// server-shared fit map to the same properties. Carries the parsed <see cref="EsiFitting"/> so the detail panel can
/// be built on selection without re-reading storage, plus the hull render, the racks, the uploader and the price.
/// Everything expensive — the render, the uploader's portrait, the equipment icons — loads on demand, so a fit that
/// is never paged to and never hovered costs nothing beyond its name.
/// </summary>
public sealed partial class FitRowViewModel : ViewModelBase
{
    private readonly ITypeImageProvider? _images;

    public string Name { get; }
    public int ShipTypeId { get; }

    /// <summary>The fit's user tags (parsed from the comma-separated metadata), empty for a server-shared row — the
    /// browser search matches these alongside the name so a tag filters the list.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>Hull name from the SDE (or <c>type {id}</c> until it is imported).</summary>
    public string ShipTypeLabel { get; }

    /// <summary>Hull class for the small label next to the name (e.g. "Frigate"), or null when the SDE has no entry
    /// .</summary>
    public string? HullClass { get; }

    public bool HasHullClass => !string.IsNullOrEmpty(HullClass);

    /// <summary>What a card's header carries when there is no render: the hull's class, set large and quiet. With
    /// CCP images switched off that band is not a picture frame waiting to be filled, it is the header — so it says
    /// something, and something that differs per fit, rather than repeating one placeholder mark down the page.
    /// Falls back to the type label, which is all there is before the SDE is imported.</summary>
    public string HullWatermark => (HasHullClass ? HullClass! : ShipTypeLabel).ToUpperInvariant();

    /// <summary>Count of fitted modules (high/mid/low/rig/subsystem); drones and cargo are excluded.</summary>
    public int ModuleCount { get; }

    /// <summary>Origin of the fit: the owning character (Local tab) or the sharer (server tab).</summary>
    public string Source { get; }

    /// <summary>Who put this fit here — the creator (owning character) on the Local tab, the sharer on a server tab
    /// . Same value as <see cref="Source"/>, named for the Uploader column.</summary>
    public string Uploader => Source;

    public EsiFitting Fit { get; }

    /// <summary>The local library DB id when this row is a locally-stored fit — the export actions (push/share)
    /// key off it. Null for a server-shared row that has not been downloaded locally.</summary>
    public int? LocalFitId { get; }

    /// <summary>Module count per rack: shown as "x modules" with a per-module tooltip.</summary>
    public int HighCount { get; }
    public int MidCount { get; }
    public int LowCount { get; }

    /// <summary>Per-rack module lines for the column tooltips (icon + name); icons load on demand.</summary>
    public IReadOnlyList<FitModuleLineViewModel> HighModules { get; }
    public IReadOnlyList<FitModuleLineViewModel> MidModules { get; }
    public IReadOnlyList<FitModuleLineViewModel> LowModules { get; }

    /// <summary>Every rack the fit actually carries — high/mid/low plus rigs, subsystems, services, drones,
    /// fighters and cargo — for the card's single equipment popover. Empty racks are left out, so the popover shows
    /// no heading with nothing under it. The first three reuse the lists the table columns already built.</summary>
    public IReadOnlyList<FitRackViewModel> Racks { get; }

    /// <summary>The popover's left column: the three module racks, which is what "what is on this fit" usually
    /// means.</summary>
    public IReadOnlyList<FitRackViewModel> ModuleRacks { get; }

    /// <summary>The popover's right column: everything else the fit carries. Two columns rather than one long
    /// list — a battleship with a full hold runs to fifty lines, which is a popover taller than the screen.</summary>
    public IReadOnlyList<FitRackViewModel> OtherRacks { get; }

    /// <summary>True when the fit carries nothing but modules — the popover then drops its second column instead of
    /// leaving an empty one.</summary>
    public bool HasOtherRacks => OtherRacks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHullImage))]
    private Bitmap? _hullImage;

    public bool HasHullImage => HullImage is not null;

    /// <summary>The big hull render behind a card, at <see cref="RenderSize"/>. Separate from
    /// <see cref="HullImage"/>: the table's 32px circle and the card's full-bleed band are different images and the
    /// provider caches them under different keys, so neither pays for the other.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHullRender))]
    private Bitmap? _hullRender;

    public bool HasHullRender => HullRender is not null;

    /// <summary>The size asked of the CCP image server for a card's render. Measured against what it actually
    /// serves: <c>render</c> answers 32/64/128/256/512/1024 and rejects anything else with HTTP 400 ("bad size"),
    /// which would leave the card blank. 512 is the smallest of those that stays sharp across the card's whole
    /// width range (roughly 300–600 logical px, more under DPI scaling) — 256 goes soft as soon as a card grows or
    /// the display scales, 1024 triples the bytes for pixels no card shows. One 512 render is ~33 KB on the wire,
    /// and the provider hands the same bitmap to every card on that hull, so the cost follows the number of
    /// distinct hulls on screen rather than the number of fits.</summary>
    public const int RenderSize = 512;

    // ── estimated fit value from the cached ESI average prices (hull + every item × quantity) ──
    private readonly IMarketPriceRepository? _prices;

    /// <summary>Summed ISK value of the fit, or null until <see cref="LoadPriceAsync"/> populates it (no repo / empty
    /// price cache leaves it null → the column shows a placeholder).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriceLabel))]
    private double? _price;

    /// <summary>The fit value formatted for the Price avg. column, or "—" while it is still unknown.</summary>
    public string PriceLabel => Price is { } value ? IskFormat.Short(value) : "—";

    // ── the uploader's portrait, beside their name on the card ──
    private readonly ICharacterPortraitProvider? _portraits;

    /// <summary>The uploader's ESI character id, or 0 when there is no character behind the name. A local fit
    /// owned by a gamelog-only pilot has no ESI id (<see cref="CharacterViewModel.CharacterId"/> is 0 for those),
    /// and an imported fit whose owner matches no known character has no character at all — both then fall back to
    /// <see cref="UploaderInitial"/> rather than showing an empty frame. Server-shared rows always have one.</summary>
    public int UploaderCharacterId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUploaderPortrait))]
    private Bitmap? _uploaderPortrait;

    public bool HasUploaderPortrait => UploaderPortrait is not null;

    /// <summary>First letter of the uploader's name, shown in place of the portrait when there is no character id,
    /// when images are off, or when the fetch fails — the same fallback the fleet rows use.</summary>
    public string UploaderInitial => string.IsNullOrEmpty(Uploader) ? "?" : Uploader[..1].ToUpperInvariant();

    // ── per-row export dropdown via the shared seam (same actions as the fit-detail header) ──
    private readonly IFitExportActions? _exportActions;
    private readonly Func<string, IReadOnlyList<CharacterPickOption>> _exportPickOptions;
    private readonly Action<string> _reportExportStatus;
    private readonly Func<string, Task>? _onSharedToServer;

    /// <summary>True when this row can be exported — it is a local fit and the seam is wired (server rows can't).</summary>
    public bool CanExport => _exportActions is not null && LocalFitId is not null;

    public ICommand ShareToServerCommand { get; }
    public ICommand PushToEveCommand { get; }
    public ICommand CopyEveshipLinkCommand { get; }
    public ICommand OpenEftWindowCommand { get; }

    // ── Fit-metadata: edit name/description/tags + delete, on local rows only. The dialog + repo + reload are
    // owned by the caller (the browser composition), reached through these callbacks — the row stays a thin carrier. ──
    private readonly Func<int, Task>? _onEditMetadata;
    private readonly Func<int, Task>? _onDelete;

    /// <summary>True when this row is a manageable local fit (it has a DB id) — server-shared rows can't be edited/deleted here.</summary>
    public bool CanManage => LocalFitId is not null;

    public ICommand EditMetadataCommand { get; }
    public ICommand DeleteCommand { get; }

    public FitRowViewModel(EsiFitting fit, string source, ISdeNameResolver names, int? localFitId = null,
        ITypeImageProvider? images = null, IFitExportActions? exportActions = null,
        Func<string, IReadOnlyList<CharacterPickOption>>? exportPickOptions = null,
        Action<string>? reportExportStatus = null, IMarketPriceRepository? prices = null,
        Func<int, Task>? onEditMetadata = null, Func<int, Task>? onDelete = null, string? tags = null,
        ICharacterPortraitProvider? portraits = null, int uploaderCharacterId = 0,
        Func<string, Task>? onSharedToServer = null)
    {
        Fit = fit;
        LocalFitId = localFitId;
        Tags = (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _images = images;
        _prices = prices;
        _portraits = portraits;
        UploaderCharacterId = uploaderCharacterId;
        _exportActions = exportActions;
        _exportPickOptions = exportPickOptions ?? (_ => []);
        _reportExportStatus = reportExportStatus ?? (_ => { });
        _onSharedToServer = onSharedToServer;
        _onEditMetadata = onEditMetadata;
        _onDelete = onDelete;
        ShareToServerCommand   = new AsyncRelayCommand(() => InvokeExportAsync((a, r) => a.ShareToServerAsync(r)), () => CanExport);
        PushToEveCommand       = new AsyncRelayCommand(() => InvokeExportAsync((a, r) => a.PushToEveAsync(r)), () => CanExport);
        CopyEveshipLinkCommand = new AsyncRelayCommand(() => InvokeExportAsync((a, r) => a.CopyEveshipLinkAsync(r)), () => CanExport);
        OpenEftWindowCommand   = new AsyncRelayCommand(() => InvokeExportAsync((a, r) => a.OpenEftWindowAsync(r)), () => CanExport);
        EditMetadataCommand    = new AsyncRelayCommand(InvokeEditMetadataAsync, () => CanManage && _onEditMetadata is not null);
        DeleteCommand          = new AsyncRelayCommand(InvokeDeleteAsync, () => CanManage && _onDelete is not null);
        Name = fit.Name;
        ShipTypeId = fit.ShipTypeId;
        ShipTypeLabel = names.TypeName(fit.ShipTypeId);
        HullClass = names.GroupName(fit.ShipTypeId);
        Source = source;

        ModuleCount = fit.Items.Count(item => FitSlotClassifier.Classify(item.Flag) is
            FitSlotCategory.High or FitSlotCategory.Medium or FitSlotCategory.Low
            or FitSlotCategory.Rig or FitSlotCategory.Subsystem);

        HighModules = BuildRack(fit, names, FitSlotCategory.High);
        MidModules = BuildRack(fit, names, FitSlotCategory.Medium);
        LowModules = BuildRack(fit, names, FitSlotCategory.Low);
        HighCount = HighModules.Count;
        MidCount = MidModules.Count;
        LowCount = LowModules.Count;

        Racks = BuildRacks(fit, names);
        ModuleRacks = Racks.Where(rack => rack.Category is
            FitSlotCategory.High or FitSlotCategory.Medium or FitSlotCategory.Low).ToList();
        OtherRacks = Racks.Except(ModuleRacks).ToList();
    }

    private List<FitModuleLineViewModel> BuildRack(EsiFitting fit, ISdeNameResolver names, FitSlotCategory category) =>
        fit.Items
            .Where(item => FitSlotClassifier.Classify(item.Flag) == category)
            .OrderBy(item => FitSlotClassifier.SlotIndex(item.Flag))
            .Select(item => new FitModuleLineViewModel(item.TypeId, names.TypeName(item.TypeId), _images, item.Quantity))
            .ToList();

    /// <summary>Every rack that carries something, in the order the popover reads them: the three module racks
    /// first, then what hangs off the fit. A rack with nothing in it is skipped rather than shown empty.</summary>
    private List<FitRackViewModel> BuildRacks(EsiFitting fit, ISdeNameResolver names)
    {
        var racks = new List<FitRackViewModel>();
        foreach (var category in PopoverRacks)
        {
            var lines = category switch
            {
                FitSlotCategory.High => HighModules,
                FitSlotCategory.Medium => MidModules,
                FitSlotCategory.Low => LowModules,
                _ => BuildRack(fit, names, category)
            };
            if (lines.Count > 0) racks.Add(new FitRackViewModel(category, Stack(lines)));
        }
        return racks;
    }

    /// <summary>
    /// Collapses a rack's identical modules onto one line with a count. Six turrets are how a fit is flown but not
    /// how it is read: listed one per line they are six rows of the same words, and they are what made the popover
    /// longer than the screen it has to sit on. The table's per-rack counts are built from the ungrouped lists and
    /// are unaffected — "6 modules" there still means six modules.
    /// </summary>
    private static List<FitModuleLineViewModel> Stack(IReadOnlyList<FitModuleLineViewModel> lines)
    {
        var stacked = new List<FitModuleLineViewModel>();
        var byType = new Dictionary<int, int>();   // type id -> index in stacked

        foreach (var line in lines)
        {
            if (byType.TryGetValue(line.TypeId, out var at))
                stacked[at] = stacked[at].Plus(line.Quantity);
            else
            {
                byType[line.TypeId] = stacked.Count;
                stacked.Add(line);
            }
        }
        return stacked;
    }

    private static readonly FitSlotCategory[] PopoverRacks =
    [
        FitSlotCategory.High, FitSlotCategory.Medium, FitSlotCategory.Low,
        FitSlotCategory.Rig, FitSlotCategory.Subsystem, FitSlotCategory.Service,
        FitSlotCategory.Drone, FitSlotCategory.Fighter, FitSlotCategory.Cargo
    ];

    /// <summary>Loads the hull render for the row icon. Gated on the images setting: the provider itself does not
    /// check it, so an ungated call fetches from CCP with images switched off.</summary>
    public Task LoadHullImageAsync() =>
        LoadImageAsync(64, bitmap => HullImage = bitmap);

    /// <summary>Loads the card's full-size hull render — on demand, so a page nobody opens fetches nothing.</summary>
    public Task LoadHullRenderAsync() =>
        LoadImageAsync(RenderSize, bitmap => HullRender = bitmap);

    private async Task LoadImageAsync(int size, Action<Bitmap?> assign)
    {
        if (_images is null || !await _images.AreImagesEnabledAsync()) return;
        assign(await _images.GetImageAsync(ShipTypeId, TypeImageKind.Render, size));
    }

    /// <summary>Loads the uploader's ESI portrait for the card — on demand, like the rest. The provider enforces the
    /// images setting itself and answers null for a character id of 0, so a row without a character behind its
    /// uploader name simply keeps its initial.</summary>
    public async Task LoadUploaderPortraitAsync()
    {
        if (_portraits is null || UploaderCharacterId <= 0) return;
        UploaderPortrait = await _portraits.GetPortraitAsync(UploaderCharacterId, PortraitSize);
    }

    /// <summary>The size asked of the image server for an uploader's portrait. <c>portrait</c> serves
    /// 32/64/128/256/512 and rejects anything else with HTTP 400 (checked, same as the hull render): 64 is the
    /// smallest that stays clean in the card's 18px circle once a display scales, and it is the size the fleet rows
    /// already ask for, so the two share a cache entry.</summary>
    public const int PortraitSize = 64;

    /// <summary>Loads every rack's icons for the card's equipment popover — on the first hover, never before.</summary>
    public async Task LoadPopoverIconsAsync()
    {
        foreach (var rack in Racks)
            await rack.LoadIconsAsync();
    }

    /// <summary>Estimates the fit value from the cached ESI average prices (hull + every item × quantity) — same
    /// sum as the fit-detail header. On demand; a missing repo or an unpopulated cache leaves the placeholder.</summary>
    public async Task LoadPriceAsync()
    {
        if (_prices is null) return;
        var typeIds = Fit.Items.Select(item => item.TypeId).Append(ShipTypeId).Distinct().ToList();
        var averages = await _prices.GetAveragePricesAsync(typeIds);
        if (averages.Count == 0) return;   // cache empty -> keep the placeholder

        var total = averages.GetValueOrDefault(ShipTypeId);
        foreach (var item in Fit.Items)
            total += averages.GetValueOrDefault(item.TypeId) * item.Quantity;
        Price = total;
    }

    /// <summary>Loads the per-module icons for one rack's tooltip — on demand, so a row that is never hovered fetches
    /// nothing.</summary>
    public async Task LoadRackIconsAsync(FitSlotCategory rack)
    {
        IReadOnlyList<FitModuleLineViewModel> lines = rack switch
        {
            FitSlotCategory.High => HighModules,
            FitSlotCategory.Medium => MidModules,
            FitSlotCategory.Low => LowModules,
            _ => []
        };
        foreach (var line in lines)
            await line.LoadImageAsync();
    }

    private async Task InvokeExportAsync(Func<IFitExportActions, FitExportRequest, Task> action)
    {
        if (_exportActions is null || LocalFitId is null) return;
        var request = new FitExportRequest(LocalFitId.Value, Name, _exportPickOptions, _reportExportStatus, _onSharedToServer);
        await action(_exportActions, request);
    }

    private async Task InvokeEditMetadataAsync()
    {
        if (_onEditMetadata is not null && LocalFitId is { } id)
            await _onEditMetadata(id);
    }

    private async Task InvokeDeleteAsync()
    {
        if (_onDelete is not null && LocalFitId is { } id)
            await _onDelete(id);
    }
}
