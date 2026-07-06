using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The reusable fit picker. <see cref="FitPickerMode.Multi"/> checks several fits and
/// returns them on ADD; <see cref="FitPickerMode.Single"/> picks one immediately through a
/// Select button. Sources are the local library (<see cref="IFittingRepository"/>), each coupled server's shared fits
/// (<see cref="ServerFitShareClient"/>) and — when scoped to a coupled composition — that doctrine's allowed fits
/// grouped by role. Each fit becomes a <see cref="FitReferenceInfo"/> snapshot.
/// </summary>
public sealed partial class FitPickerViewModel : ObservableObject
{
    private const string CompositionSource = "Composition";
    private const string LocalSource = "Local";
    private const string ServerSource = "Server";

    private readonly IServiceProvider _services;
    private readonly HashSet<string> _alreadyAddedHashes;
    private readonly FleetCompositionDetail? _composition;
    private readonly string? _currentFitHash;
    private readonly int? _skillCheckCharacterId;
    private readonly IMemberFitSkillEvaluator? _skillEvaluator;

    private readonly List<FitPickerRowViewModel> _local = [];
    private readonly List<FitPickerRowViewModel> _server = [];
    private readonly List<FitPickerRoleGroupViewModel> _compositionGroups = [];
    private Task? _loadTask;
    private ITypeImageProvider? _images;

    /// <summary>Multi-select picker over the local + server libraries.</summary>
    public FitPickerViewModel(IServiceProvider services, IEnumerable<string>? alreadyAdded = null)
        : this(services, FitPickerMode.Multi, alreadyAdded, composition: null, currentFitHash: null)
    {
    }

    /// <param name="services">Service provider the picker resolves its dependencies (repositories, dialogs, image provider) from.</param>
    /// <param name="mode">Multi (checkboxes + ADD) or Single (Select per row).</param>
    /// <param name="alreadyAdded">Content hashes already in the target group — listed but not selectable.</param>
    /// <param name="composition">When set, adds a Composition source (default) with the doctrine's fits grouped by role.</param>
    /// <param name="currentFitHash">The member's current assignment (single mode) — shown as the "current" badge.</param>
    /// <param name="skillCheckCharacterId">The character a single-mode assign is for — each row gets a can-fly / warning
    /// badge against this character's skills. Null (e.g. the composition editor) shows no per-row badge.</param>
    public FitPickerViewModel(IServiceProvider services, FitPickerMode mode, IEnumerable<string>? alreadyAdded,
        FleetCompositionDetail? composition, string? currentFitHash, int? skillCheckCharacterId = null)
    {
        _services = services;
        Mode = mode;
        _alreadyAddedHashes = new HashSet<string>(alreadyAdded ?? [], StringComparer.OrdinalIgnoreCase);
        _composition = composition;
        _currentFitHash = currentFitHash;
        _skillCheckCharacterId = skillCheckCharacterId;
        _skillEvaluator = skillCheckCharacterId is null ? null : services.GetService<IMemberFitSkillEvaluator>();
        _source = composition is not null ? CompositionSource : LocalSource;
        _ = EnsureLoadedAsync();
    }

    public FitPickerMode Mode { get; }
    public bool IsMulti => Mode == FitPickerMode.Multi;
    public bool IsSingle => Mode == FitPickerMode.Single;
    public bool HasComposition => _composition is not null;
    public string CompositionName => _composition?.Composition.Name ?? "";
    public string HeaderTitle => IsMulti ? "ADD FITS" : "ASSIGN A FIT";
    public string HeaderSub => HasComposition ? CompositionName : "ADD TO ROLE";

    /// <summary>Raised in single-select mode when a fit is picked — the window closes with it.</summary>
    public event Action<FitReferenceInfo>? FitPicked;

    /// <summary>Flat rows of the active Local/Server source after the search filter.</summary>
    public ObservableCollection<FitPickerRowViewModel> Rows { get; } = [];

    /// <summary>Role-grouped rows of the Composition source after the search filter.</summary>
    public ObservableCollection<FitPickerRoleGroupViewModel> RoleGroups { get; } = [];

    [ObservableProperty] private string _source;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isEmpty;

    public bool IsCompositionSource => Source == CompositionSource;
    public bool IsLocalSource => Source == LocalSource;
    public bool IsServerSource => Source == ServerSource;
    public bool CanConfirm => SelectedCount > 0;

    partial void OnSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsCompositionSource));
        OnPropertyChanged(nameof(IsLocalSource));
        OnPropertyChanged(nameof(IsServerSource));
        _ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => _ApplyFilter();

    [RelayCommand] private void ShowComposition() => Source = CompositionSource;
    [RelayCommand] private void ShowLocal() => Source = LocalSource;
    [RelayCommand] private void ShowServer() => Source = ServerSource;

    /// <summary>Loads every source once; repeat calls return the same task (idempotent).</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    public async Task LoadAsync()
    {
        _images = _services.GetService<ITypeImageProvider>();
        await _LoadLocalAsync();
        await _LoadServerAsync();
        _BuildCompositionGroups();
        _ApplyFilter();
    }

    /// <summary>Opens the read-only radial fit-detail for a row's fit.</summary>
    [RelayCommand]
    private Task OpenDetail(FitPickerRowViewModel? row) =>
        row is null ? Task.CompletedTask : FitDetailLauncher.OpenAsync(_services, _services.GetRequiredService<IDialogService>(), row.Fit);

    /// <summary>Whole-row activation: multi toggles selection, single picks the fit immediately. A fit already in the
    /// group, or the current assignment, is inert.</summary>
    [RelayCommand]
    private void ActivateRow(FitPickerRowViewModel? row)
    {
        if (row is null || row.AlreadyAdded || row.IsCurrent)
            return;
        if (IsMulti)
            row.IsSelected = !row.IsSelected;
        else
            FitPicked?.Invoke(row.Fit);
    }

    private async Task _LoadLocalAsync()
    {
        var fittings = await _services.GetRequiredService<IFittingRepository>().ListAllAsync();
        var names = (await _services.GetRequiredService<ICharacterRegistry>().GetAllAsync())
            .Where(c => c.EsiCharacterId is not null)
            .ToDictionary(c => c.EsiCharacterId!.Value, c => c.Name);
        var resolver = FitNameResolverFactory.For(_services);

        foreach (var fit in fittings)
        {
            var hash = string.IsNullOrEmpty(fit.ContentHash) ? FitContentHash.Compute(fit.RawJson) : fit.ContentHash;
            var reference = new FitReferenceInfo(fit.ShipTypeId, fit.Name, fit.RawJson, hash, fit.Id, null);
            var owner = int.TryParse(fit.OwnerId, out var ownerId) && names.TryGetValue(ownerId, out var name) ? name : "—";
            _local.Add(_Row(reference, resolver, LocalSource, owner));
        }
    }

    private async Task _LoadServerAsync()
    {
        var fitShare = _services.GetRequiredService<ServerFitShareClient>();
        var sessions = _services.GetRequiredService<IClientSessionStore>();
        var resolver = FitNameResolverFactory.For(_services);

        foreach (var server in await sessions.ListServersAsync())
        {
            var (ok, _, fits) = await fitShare.GetSharedFitsAsync(server);
            if (!ok)
                continue;
            foreach (var fit in fits)
            {
                var reference = new FitReferenceInfo(fit.ShipTypeId, fit.Name, fit.RawJson, FitContentHash.Compute(fit.RawJson), null, fit.ServerId);
                _server.Add(_Row(reference, resolver, ServerSource, fit.SharedByCharacterName));
            }
        }
    }

    private void _BuildCompositionGroups()
    {
        if (_composition is null)
            return;
        var resolver = FitNameResolverFactory.For(_services);
        foreach (var role in _composition.Roles)
        {
            var rows = role.Entries.Select(e => _Row(e.Fit, resolver, CompositionSource, CompositionName)).ToList();
            if (rows.Count > 0)
                _compositionGroups.Add(new FitPickerRoleGroupViewModel(role.RoleName, _MinLabel(role), rows));
        }
    }

    private static string _MinLabel(FleetCompositionRoleInfo role)
    {
        if (role.GroupMinCount is int group)
            return "≥" + group;
        var perFit = role.Entries.Where(e => e.EntryMinCount is int).Select(e => e.EntryMinCount!.Value).ToList();
        return perFit.Count > 0 ? string.Join("+", perFit) : "—";
    }

    private FitPickerRowViewModel _Row(FitReferenceInfo reference, ISdeNameResolver resolver, string source, string owner)
    {
        var row = new FitPickerRowViewModel(reference, resolver.TypeName(reference.ShipTypeId), source, owner,
            _alreadyAddedHashes.Contains(reference.ContentHash), _images)
        {
            IsCurrent = _currentFitHash is not null
                        && string.Equals(reference.ContentHash, _currentFitHash, StringComparison.OrdinalIgnoreCase)
        };
        row.PropertyChanged += _OnRowChanged;
        _ = row.LoadHullImageAsync();
        if (_skillEvaluator is not null && _skillCheckCharacterId is int characterId)
            _ = row.LoadSkillBadgeAsync(_skillEvaluator, characterId);
        return row;
    }

    private void _OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FitPickerRowViewModel.IsSelected))
            _RecountSelection();
    }

    private void _RecountSelection()
    {
        SelectedCount = _local.Concat(_server).Count(r => r.IsSelected);
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void _ApplyFilter()
    {
        var term = SearchText.Trim();
        bool Match(FitPickerRowViewModel r) => term.Length == 0
            || r.FitName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || r.HullName.Contains(term, StringComparison.OrdinalIgnoreCase);

        if (IsCompositionSource)
        {
            // Re-project the master groups (same row instances, so selection/current state is kept), dropping empty ones.
            RoleGroups.Clear();
            foreach (var group in _compositionGroups)
            {
                var rows = group.Rows.Where(Match).ToList();
                if (rows.Count > 0)
                    RoleGroups.Add(new FitPickerRoleGroupViewModel(group.RoleName, group.MinLabel, rows));
            }
            Rows.Clear();
            IsEmpty = RoleGroups.Count == 0;
            return;
        }

        var source = IsLocalSource ? _local : _server;
        Rows.Clear();
        foreach (var row in source.Where(Match))
            Rows.Add(row);
        RoleGroups.Clear();
        IsEmpty = Rows.Count == 0;
    }

    /// <summary>The snapshots of every selected fit across the flat sources — the picker's result on ADD (multi mode).</summary>
    public IReadOnlyList<FitReferenceInfo> SelectedFits() =>
        _local.Concat(_server).Where(r => r.IsSelected).Select(r => r.Fit).ToList();
}
