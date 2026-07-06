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
/// One row in the fit-browser DataGrid. Uniform across the Local and server tabs: a Local fit and a
/// server-shared fit map to the same columns. Carries the parsed <see cref="EsiFitting"/> so the detail panel can be
/// built on selection without re-reading storage, plus the hull render, per-rack module counts + tooltip lists
/// and the uploader, all for the table columns. Images load on demand so an unseen row fetches nothing.
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHullImage))]
    private Bitmap? _hullImage;

    public bool HasHullImage => HullImage is not null;

    // ── estimated fit value from the cached ESI average prices (hull + every item × quantity) ──
    private readonly IMarketPriceRepository? _prices;

    /// <summary>Summed ISK value of the fit, or null until <see cref="LoadPriceAsync"/> populates it (no repo / empty
    /// price cache leaves it null → the column shows a placeholder).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriceLabel))]
    private double? _price;

    /// <summary>The fit value formatted for the Price avg. column, or "—" while it is still unknown.</summary>
    public string PriceLabel => Price is { } value ? IskFormat.Short(value) : "—";

    // ── per-row export dropdown via the shared seam (same actions as the fit-detail header) ──
    private readonly IFitExportActions? _exportActions;
    private readonly Func<string, IReadOnlyList<CharacterPickOption>> _exportPickOptions;
    private readonly Action<string> _reportExportStatus;

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
        Func<int, Task>? onEditMetadata = null, Func<int, Task>? onDelete = null, string? tags = null)
    {
        Fit = fit;
        LocalFitId = localFitId;
        Tags = (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _images = images;
        _prices = prices;
        _exportActions = exportActions;
        _exportPickOptions = exportPickOptions ?? (_ => []);
        _reportExportStatus = reportExportStatus ?? (_ => { });
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
    }

    private List<FitModuleLineViewModel> BuildRack(EsiFitting fit, ISdeNameResolver names, FitSlotCategory category) =>
        fit.Items
            .Where(item => FitSlotClassifier.Classify(item.Flag) == category)
            .OrderBy(item => FitSlotClassifier.SlotIndex(item.Flag))
            .Select(item => new FitModuleLineViewModel(item.TypeId, names.TypeName(item.TypeId), _images))
            .ToList();

    /// <summary>Loads the hull render for the row icon — on demand, opt-in CCP images.</summary>
    public async Task LoadHullImageAsync() =>
        HullImage = _images is null ? null : await _images.GetImageAsync(ShipTypeId, TypeImageKind.Render, 64);

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
        var request = new FitExportRequest(LocalFitId.Value, Name, _exportPickOptions, _reportExportStatus);
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
