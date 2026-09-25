using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The create/edit composition dialog: name + description and a list of role groups, each with an
/// optional group minimum and fit entries (added through the reusable <see cref="FitPickerViewModel"/>) that may carry
/// an optional per-fit minimum and doctrine skill minimums. Edits are tentative — the editor works on a mutable copy of the composition graph and,
/// on save, diffs against the loaded snapshot and replays the minimal set of granular commands through
/// <see cref="IFleetCompositionClient"/> (cancel discards). New roles/entries have no id until they are saved.
///
/// The composition can change under an open editor (another window, another client). With nothing edited yet the
/// editor quietly reloads; with unsaved edits it never overwrites them and offers a reload instead.
/// </summary>
public sealed partial class CompositionEditorViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IFleetCompositionClient _client;
    private readonly IDialogService _dialogs;
    private readonly ISdeNameResolver _resolver;
    private readonly ITypeImageProvider? _images;
    private readonly IFitValidator? _validator;
    private readonly Dictionary<string, int> _skillIdsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly long? _compositionId;
    private readonly Guid _newCompositionId = Guid.NewGuid();
    private readonly IDisposable? _changeSubscription;
    private FleetCompositionDetail? _snapshot;
    private bool _isSaving;

    private CompositionEditorViewModel(IServiceProvider services, IFleetCompositionClient client, FleetCompositionDetail? snapshot,
        bool isReadOnly = false)
    {
        _services = services;
        _client = client;
        _dialogs = services.GetRequiredService<IDialogService>();
        _resolver = FitNameResolverFactory.For(services);
        _images = services.GetRequiredService<ITypeImageProvider>();
        _validator = services.GetService<IFitValidator>();
        _compositionId = snapshot?.Composition.Id;
        IsReadOnly = isReadOnly;
        SkillNames = _LoadSkillNames(services.GetService<ISdeAccessor>());

        if (snapshot is not null)
            _Load(snapshot);

        Roles.CollectionChanged += _OnRolesChanged;
        _Recompute();

        if (_compositionId is not null)
            _changeSubscription = services.GetService<CompositionChangeFeed>()?.Subscribe(_OnCompositionsChangedAsync);
    }

    public void Dispose() => _changeSubscription?.Dispose();

    /// <summary>A blank editor that creates a new composition through <paramref name="client"/> on save.</summary>
    public static CompositionEditorViewModel ForNew(IServiceProvider services, IFleetCompositionClient client) =>
        new(services, client, snapshot: null);

    /// <summary>An editor pre-filled from an existing composition graph, edited in place on save.</summary>
    public static CompositionEditorViewModel ForExisting(IServiceProvider services, IFleetCompositionClient client, FleetCompositionDetail detail) =>
        new(services, client, detail);

    /// <summary>A read-only view of someone else's composition: the same nested role/entry layout and
    /// the fit-detail doorklik, but no edit affordances or save — viewing + opening a fit is allowed for everyone.</summary>
    public static CompositionEditorViewModel ForView(IServiceProvider services, IFleetCompositionClient client, FleetCompositionDetail detail) =>
        new(services, client, detail, isReadOnly: true);

    /// <summary>Raised when the dialog should close: true if the composition was saved, false on cancel.</summary>
    public event Action<bool>? CloseRequested;

    public bool IsNew => _compositionId is null;
    // A new composition keeps its generated identity until Save() closes the window; otherwise the same
    // composition could exist once under the new id and once under the persisted composition id.
    public string ModuleId => _compositionId is { } compositionId
        ? $"composition-editor:{compositionId}"
        : $"composition-editor:new:{_newCompositionId}";

    /// <summary>Read-only view (someone else's composition) — the edit affordances and save are hidden.</summary>
    public bool IsReadOnly { get; }
    public bool IsEditable => !IsReadOnly;
    public bool IsSaveAvailable => IsEditable && !IsDeletedElsewhere;

    public string Title => IsReadOnly ? "View composition" : IsNew ? "New composition" : "Edit composition";
    public string CancelButtonLabel => IsReadOnly ? "CLOSE" : "CANCEL";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _status = "";

    /// <summary>The composition changed elsewhere while this editor holds unsaved edits — <see cref="ReloadCommand"/> takes the new version.</summary>
    [ObservableProperty] private bool _hasRemoteChange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveAvailable))]
    private bool _isDeletedElsewhere;

    [ObservableProperty] private int _roleCount;
    [ObservableProperty] private int _fitCount;
    [ObservableProperty] private int _minPilots;

    public ObservableCollection<EditorRoleViewModel> Roles { get; } = [];

    /// <summary>Every published skill (SDE category 16) by name, for the DOCTRINE MINIMUM picker.</summary>
    public IReadOnlyList<string> SkillNames { get; }

    [RelayCommand]
    private void AddRoleGroup() => _Track(new EditorRoleViewModel(id: null, roleName: "New role", groupMinCount: null));

    [RelayCommand]
    private void RemoveRole(EditorRoleViewModel? role)
    {
        if (role is not null)
            _Untrack(role);
    }

    [RelayCommand]
    private async Task AddFit(EditorRoleViewModel? role)
    {
        if (role is null)
            return;

        var alreadyAdded = role.Entries.Select(e => e.Fit.ContentHash);
        var picked = await _dialogs.ShowFitPickerAsync(new FitPickerViewModel(_services, alreadyAdded));
        if (picked is null)
            return;

        foreach (var fit in picked)
        {
            if (role.Entries.Any(e => string.Equals(e.Fit.ContentHash, fit.ContentHash, StringComparison.OrdinalIgnoreCase)))
                continue;
            role.Add(_NewEntry(id: null, fit, entryMinCount: null, skillMinimums: []));
        }
        _Recompute();
    }

    /// <summary>Opens the read-only radial fit-detail for an entry's fit.</summary>
    [RelayCommand]
    private Task OpenFitDetail(EditorEntryViewModel? entry) =>
        entry is null ? Task.CompletedTask : FitDetailLauncher.OpenAsync(_services, _dialogs, entry.Fit);

    /// <summary>Adds the skill typed into an entry's DOCTRINE MINIMUM row at the chosen level. An unknown name
    /// only sets the status; nothing is sent until save.</summary>
    [RelayCommand]
    private void AddSkillMinimum(EditorEntryViewModel? entry)
    {
        if (entry is null || IsReadOnly)
        {
            return;
        }

        if (!_skillIdsByName.TryGetValue(entry.NewSkillText.Trim(), out var skillTypeId))
        {
            Status = "Pick a skill from the list.";
            return;
        }

        entry.AddSkillMinimum(skillTypeId, _resolver.TypeName(skillTypeId), entry.NewSkillLevelIndex + 1);
        entry.NewSkillText = "";
        Status = "";
    }

    /// <summary>Builds an entry view-model with the hull render kicked off on demand.</summary>
    private EditorEntryViewModel _NewEntry(long? id, FitReferenceInfo fit, int? entryMinCount, IReadOnlyList<SkillMinimum> skillMinimums)
    {
        var entry = new EditorEntryViewModel(id, fit, _resolver.TypeName(fit.ShipTypeId), entryMinCount, _images,
            skillMinimums, _FitSkillLevels(fit), _resolver.TypeName);
        _ = entry.LoadHullImageAsync();
        return entry;
    }

    /// <summary>The level the fit itself requires of each skill (its whole prerequisite closure), for the "fit needs"
    /// hint. Empty when there is no validator or the snapshot is not a readable fit.</summary>
    private IReadOnlyDictionary<int, int> _FitSkillLevels(FitReferenceInfo fit)
    {
        EsiFitting? fitting;
        try
        {
            fitting = JsonSerializer.Deserialize<EsiFitting>(fit.RawJson);
        }
        catch (JsonException)
        {
            fitting = null;
        }

        if (_validator is null || fitting?.Items is null)
        {
            return new Dictionary<int, int>();
        }

        return _validator.ValidateSkills(fitting, new Dictionary<int, int>())
            .ToDictionary(gap => gap.SkillTypeId, gap => gap.RequiredLevel);
    }

    private IReadOnlyList<string> _LoadSkillNames(ISdeAccessor? sde)
    {
        if (sde is not { IsAvailable: true })
        {
            return [];
        }

        foreach (var group in sde.GetGroupsByCategory(16))
        {
            foreach (var skill in sde.GetSkillsInGroup(group.GroupId))
            {
                _skillIdsByName.TryAdd(skill.Name, skill.TypeId);
            }
        }

        return [.. _skillIdsByName.Keys.Order(StringComparer.OrdinalIgnoreCase)];
    }

    [RelayCommand]
    private void RemoveEntry(EditorEntryViewModel? entry)
    {
        if (entry is null)
            return;
        var role = Roles.FirstOrDefault(r => r.Entries.Contains(entry));
        role?.Remove(entry);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    [RelayCommand]
    private async Task Reload()
    {
        if (_compositionId is not { } compositionId)
            return;

        var detail = await _client.GetAsync(compositionId);
        if (detail is null)
        {
            Status = "Couldn't reload this composition.";
            return;
        }

        _Load(detail);
        HasRemoteChange = false;
        Status = "";
    }

    [RelayCommand]
    private async Task Save()
    {
        if (IsReadOnly || IsDeletedElsewhere)
            return;

        var name = Name.Trim();
        if (name.Length == 0)
        {
            Status = "Give the composition a name.";
            return;
        }
        if (Roles.Any(r => r.RoleName.Trim().Length == 0))
        {
            Status = "Every role group needs a name.";
            return;
        }

        if (!await _ConfirmServerFitShareAsync())
            return;

        var description = _NullIfBlank(Description);
        _isSaving = true;
        try
        {
            if (!await _PersistAsync(name, description))
                return;
        }
        finally
        {
            _isSaving = false;
        }

        CloseRequested?.Invoke(true);
    }

    private void _Load(FleetCompositionDetail detail)
    {
        _snapshot = detail;
        Name = detail.Composition.Name;
        Description = detail.Composition.Description ?? "";

        foreach (var role in Roles.ToList())
            _Untrack(role);
        foreach (var role in detail.Roles)
        {
            var roleVm = new EditorRoleViewModel(role.Id, role.RoleName, role.GroupMinCount);
            foreach (var entry in role.Entries)
                roleVm.Add(_NewEntry(entry.Id, entry.Fit, entry.EntryMinCount, entry.SkillMinimums));
            _Track(roleVm);
        }
    }

    // Our own save publishes for every command it replays; those are not news to this editor. Of a burst, the last change
    // about this composition decides what happens.
    private Task _OnCompositionsChangedAsync(IReadOnlyList<CompositionChangedEvent> changes)
    {
        if (_isSaving || _compositionId is not { } compositionId)
            return Task.CompletedTask;

        var latest = changes.LastOrDefault(change =>
            change.Data.CompositionId == compositionId && change.Data.IsClientOnly != _client.SharesFitsToServer);
        return latest is null ? Task.CompletedTask : _HandleRemoteChangeAsync(latest.Data.Kind);
    }

    private async Task _HandleRemoteChangeAsync(CompositionChangeKind kind)
    {
        if (_compositionId is not { } compositionId)
            return;

        if (kind is CompositionChangeKind.Deleted)
        {
            IsDeletedElsewhere = true;
            HasRemoteChange = false;
            Status = "Deleted elsewhere — this composition no longer exists.";
            return;
        }

        if (_HasUnsavedChanges())
        {
            HasRemoteChange = true;
            Status = "Changed elsewhere — reload";
            return;
        }

        var detail = await _client.GetAsync(compositionId);
        // Edits may have started while the read was in flight; they win over a silent reload.
        if (detail is null || _HasUnsavedChanges())
            return;

        _Load(detail);
    }

    private bool _HasUnsavedChanges()
    {
        if (_snapshot is null)
            return false;

        if (Name.Trim() != _snapshot.Composition.Name || _NullIfBlank(Description) != _snapshot.Composition.Description)
            return true;
        if (Roles.Count != _snapshot.Roles.Count)
            return true;

        foreach (var role in Roles)
        {
            var snapRole = _snapshot.Roles.FirstOrDefault(r => r.Id == role.Id);
            if (snapRole is null
                || role.RoleName.Trim() != snapRole.RoleName
                || role.GroupMinCount != snapRole.GroupMinCount
                || role.Entries.Count != snapRole.Entries.Count)
                return true;

            foreach (var entry in role.Entries)
            {
                var snapEntry = snapRole.Entries.FirstOrDefault(e => e.Id == entry.Id);
                if (snapEntry is null || entry.EntryMinCount != snapEntry.EntryMinCount
                    || !_SameSkillMinimums(entry.SkillMinimumList, snapEntry.SkillMinimums))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Opsec/privacy gate: saving onto a server-backed library sends a full self-contained copy of
    /// each newly added fit (ship + modules) to the server, where anyone with access can view it — the same exposure as
    /// a push (<see cref="CompositionsViewModel"/>). Confirm the intent before any fit leaves the machine. Only fires for
    /// a server target that is actually adding fits; a local save or a metadata-only edit (no new entries) shares nothing
    /// and prompts nothing.</summary>
    private async Task<bool> _ConfirmServerFitShareAsync()
    {
        if (!_client.SharesFitsToServer)
            return true;

        // Existing entries on a server composition are already shared; only NEW entries (no id) send fresh fits.
        var newFits = Roles.SelectMany(r => r.Entries)
            .Where(e => e.Id is null)
            .Select(e => e.Fit.ContentHash)
            .Distinct()
            .Count();
        if (newFits == 0)
            return true;

        if (await _dialogs.ConfirmAsync(
                "Share fits with a server?",
                $"Saving this composition sends a full copy of {newFits} fit{(newFits == 1 ? "" : "s")} (ship + modules) " +
                "to the server, where anyone with access to that server can view them. Only save doctrines whose fits you " +
                "are comfortable sharing.",
                okText: "Save & share"))
            return true;

        Status = "Save cancelled — no fits were shared.";
        return false;
    }

    /// <summary>Diffs the working copy against the loaded snapshot and replays the minimal granular commands. Returns
    /// false (and leaves <see cref="Status"/> set) on the first failure, without closing.</summary>
    private async Task<bool> _PersistAsync(string name, string? description)
    {
        // 1. Composition header — create (new) or edit (changed).
        long compositionId;
        if (_compositionId is null)
        {
            var (ok, message, id) = await _client.CreateAsync(name, description);
            if (!ok)
                return _Fail(message);
            compositionId = id;
        }
        else
        {
            compositionId = _compositionId.Value;
            if (name != _snapshot!.Composition.Name || description != _snapshot.Composition.Description)
            {
                var (ok, message) = await _client.EditAsync(compositionId, name, description);
                if (!ok)
                    return _Fail(message);
            }
        }

        // 2. Removed role groups (in the snapshot, gone from the working copy) — cascade drops their entries.
        var workingRoleIds = Roles.Where(r => r.Id is long).Select(r => r.Id!.Value).ToHashSet();
        foreach (var snapRole in _snapshot?.Roles ?? [])
            if (!workingRoleIds.Contains(snapRole.Id))
            {
                var (ok, message) = await _client.RemoveRoleAsync(snapRole.Id);
                if (!ok)
                    return _Fail(message);
            }

        // 3. Each working role group — add (new) or edit (changed), then reconcile its entries.
        foreach (var role in Roles)
        {
            var roleName = role.RoleName.Trim();
            long roleId;
            FleetCompositionRoleInfo? snapRole = null;

            if (role.Id is null)
            {
                var (ok, message, id) = await _client.AddRoleAsync(compositionId, roleName, role.GroupMinCount);
                if (!ok)
                    return _Fail(message);
                roleId = id;
            }
            else
            {
                roleId = role.Id.Value;
                snapRole = _snapshot!.Roles.First(r => r.Id == roleId);
                if (roleName != snapRole.RoleName || role.GroupMinCount != snapRole.GroupMinCount)
                {
                    var (ok, message) = await _client.EditRoleAsync(roleId, roleName, role.GroupMinCount);
                    if (!ok)
                        return _Fail(message);
                }

                // Removed entries within this role.
                var workingEntryIds = role.Entries.Where(e => e.Id is long).Select(e => e.Id!.Value).ToHashSet();
                foreach (var snapEntry in snapRole.Entries)
                    if (!workingEntryIds.Contains(snapEntry.Id))
                    {
                        var (ok, message) = await _client.RemoveEntryAsync(snapEntry.Id);
                        if (!ok)
                            return _Fail(message);
                    }
            }

            // Entries: add new ones, edit a changed per-fit minimum or skill minimums (the fit snapshot itself never
            // changes). Unchanged skill minimums are not sent, so the server keeps what it has.
            foreach (var entry in role.Entries)
            {
                if (entry.Id is not { } entryId)
                {
                    var (ok, message, _) = await _client.AddEntryAsync(roleId, entry.Fit, entry.EntryMinCount, entry.SkillMinimumList);
                    if (!ok)
                        return _Fail(message);
                }
                else if (snapRole is not null)
                {
                    var snapEntry = snapRole.Entries.First(e => e.Id == entryId);
                    var skillMinimums = entry.SkillMinimumList;
                    var skillMinimumsChanged = !_SameSkillMinimums(skillMinimums, snapEntry.SkillMinimums);
                    if (entry.EntryMinCount != snapEntry.EntryMinCount || skillMinimumsChanged)
                    {
                        var (ok, message) = await _client.EditEntryAsync(entryId, entry.EntryMinCount,
                            skillMinimumsChanged ? skillMinimums : null);
                        if (!ok)
                            return _Fail(message);
                    }
                }
            }
        }

        return true;
    }

    private bool _Fail(string message)
    {
        Status = message;
        return false;
    }

    private void _Track(EditorRoleViewModel role)
    {
        role.Changed += _Recompute;
        Roles.Add(role);
    }

    private void _Untrack(EditorRoleViewModel role)
    {
        role.Changed -= _Recompute;
        Roles.Remove(role);
    }

    private void _OnRolesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _Recompute();

    private void _Recompute()
    {
        RoleCount = Roles.Count;
        FitCount = Roles.Sum(r => r.Entries.Count);
        MinPilots = Roles.Sum(r => r.Requirement);
    }

    private static bool _SameSkillMinimums(IReadOnlyList<SkillMinimum> working, IReadOnlyList<SkillMinimum> saved) =>
        working.Count == saved.Count && working.All(saved.Contains);

    private static string? _NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
