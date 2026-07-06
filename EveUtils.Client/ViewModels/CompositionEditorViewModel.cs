using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.FitBrowser;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The create/edit composition dialog: name + description and a list of role groups, each with an
/// optional group minimum and fit entries (added through the reusable <see cref="FitPickerViewModel"/>) that may carry
/// an optional per-fit minimum. Edits are tentative — the editor works on a mutable copy of the composition graph and,
/// on save, diffs against the loaded snapshot and replays the minimal set of granular commands through
/// <see cref="IFleetCompositionClient"/> (cancel discards). New roles/entries have no id until they are saved.
/// </summary>
public sealed partial class CompositionEditorViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IFleetCompositionClient _client;
    private readonly IDialogService _dialogs;
    private readonly ISdeNameResolver _resolver;
    private readonly ITypeImageProvider? _images;
    private readonly FleetCompositionDetail? _snapshot;
    private readonly long? _compositionId;

    private CompositionEditorViewModel(IServiceProvider services, IFleetCompositionClient client, FleetCompositionDetail? snapshot,
        bool isReadOnly = false)
    {
        _services = services;
        _client = client;
        _dialogs = services.GetRequiredService<IDialogService>();
        _resolver = FitNameResolverFactory.For(services);
        _images = services.GetRequiredService<ITypeImageProvider>();
        _snapshot = snapshot;
        _compositionId = snapshot?.Composition.Id;
        IsReadOnly = isReadOnly;

        if (snapshot is not null)
        {
            _name = snapshot.Composition.Name;
            _description = snapshot.Composition.Description ?? "";
            foreach (var role in snapshot.Roles)
            {
                var roleVm = new EditorRoleViewModel(role.Id, role.RoleName, role.GroupMinCount);
                foreach (var entry in role.Entries)
                    roleVm.Add(_NewEntry(entry.Id, entry.Fit, entry.EntryMinCount));
                _Track(roleVm);
            }
        }

        Roles.CollectionChanged += _OnRolesChanged;
        _Recompute();
    }

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

    /// <summary>Read-only view (someone else's composition) — the edit affordances and save are hidden.</summary>
    public bool IsReadOnly { get; }
    public bool IsEditable => !IsReadOnly;

    public string Title => IsReadOnly ? "View composition" : IsNew ? "New composition" : "Edit composition";
    public string CancelButtonLabel => IsReadOnly ? "CLOSE" : "CANCEL";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _status = "";

    [ObservableProperty] private int _roleCount;
    [ObservableProperty] private int _fitCount;
    [ObservableProperty] private int _minPilots;

    public ObservableCollection<EditorRoleViewModel> Roles { get; } = [];

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
            role.Add(_NewEntry(id: null, fit, entryMinCount: null));
        }
        _Recompute();
    }

    /// <summary>Opens the read-only radial fit-detail for an entry's fit.</summary>
    [RelayCommand]
    private Task OpenFitDetail(EditorEntryViewModel? entry) =>
        entry is null ? Task.CompletedTask : FitDetailLauncher.OpenAsync(_services, _dialogs, entry.Fit);

    /// <summary>Builds an entry view-model with the hull render kicked off on demand.</summary>
    private EditorEntryViewModel _NewEntry(long? id, FitReferenceInfo fit, int? entryMinCount)
    {
        var entry = new EditorEntryViewModel(id, fit, _resolver.TypeName(fit.ShipTypeId), entryMinCount, _images);
        _ = entry.LoadHullImageAsync();
        return entry;
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
    private async Task Save()
    {
        if (IsReadOnly)
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
        if (!await _PersistAsync(name, description))
            return;

        CloseRequested?.Invoke(true);
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

            // Entries: add new ones, edit a changed per-fit minimum (the fit snapshot itself never changes).
            foreach (var entry in role.Entries)
            {
                if (entry.Id is null)
                {
                    var (ok, message, _) = await _client.AddEntryAsync(roleId, entry.Fit, entry.EntryMinCount);
                    if (!ok)
                        return _Fail(message);
                }
                else if (snapRole is not null)
                {
                    var snapEntry = snapRole.Entries.First(e => e.Id == entry.Id);
                    if (entry.EntryMinCount != snapEntry.EntryMinCount)
                    {
                        var (ok, message) = await _client.EditEntryAsync(entry.Id.Value, entry.EntryMinCount);
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

    private static string? _NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
