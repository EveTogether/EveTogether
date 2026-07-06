using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Messaging;
using EveUtils.Client.Transport;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The Fleet Compositions library: a docked module that lists reusable doctrines on a Local library tab
/// plus one tab per coupled server (mirroring the fit browser's Local + tab-per-server). Each tab loads lazily
/// on first selection through its own facade — local repository or the gRPC seam — and surfaces a status line so a
/// disconnected or empty server reads clearly instead of silently. Drives create / open (editor) / delete and the
/// push-to-server / download-to-local transfer, the composition analogue of fit sharing.
/// </summary>
public sealed partial class CompositionsViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ClientFleetService _localFleets;
    private readonly IFleetCompositionRepository _compositionRepository;
    private readonly ICharacterRegistry _characters;
    private readonly IClientSessionStore _sessions;
    private readonly IFleetTransportClient _transport;
    private readonly IDialogService _dialogs;

    private Task? _initTask;

    public CompositionsViewModel(IServiceProvider services)
    {
        _services = services;
        _localFleets = services.GetRequiredService<ClientFleetService>();
        _compositionRepository = services.GetRequiredService<IFleetCompositionRepository>();
        _characters = services.GetRequiredService<ICharacterRegistry>();
        _sessions = services.GetRequiredService<IClientSessionStore>();
        _transport = services.GetRequiredService<IFleetTransportClient>();
        _dialogs = services.GetRequiredService<IDialogService>();

        _ = _EnsureInitializedAsync();
    }

    /// <summary>Local library first, then one tab per coupled server.</summary>
    public ObservableCollection<CompositionTabViewModel> Tabs { get; } = [];

    [ObservableProperty] private CompositionTabViewModel? _selectedTab;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "";

    partial void OnSearchTextChanged(string value) => SelectedTab?.SetFilter(value);

    partial void OnSelectedTabChanged(CompositionTabViewModel? value)
    {
        if (value is null)
            return;
        value.SetFilter(SearchText);
        _ = value.EnsureLoadedAsync();
    }

    /// <summary>Builds the tab set once (Local + a tab per coupled server) and selects the Local tab.</summary>
    private Task _EnsureInitializedAsync() => _initTask ??= _InitializeAsync();

    private async Task _InitializeAsync()
    {
        Tabs.Add(new CompositionTabViewModel("Local library", isLocal: true, serverAddress: null, _LoadLocalTabAsync));

        var registry = _services.GetService<IServerRegistry>();
        foreach (var server in await _sessions.ListServersAsync())
        {
            var display = registry is null ? server : await registry.DisplayNameAsync(server);
            Tabs.Add(new CompositionTabViewModel(display, isLocal: false, server, _LoadServerTabAsync));
        }

        SelectedTab = Tabs[0];
    }

    /// <summary>Awaits the tab set then reloads the active tab — the entry point for tests and post-mutation refreshes.</summary>
    public async Task ReloadAsync()
    {
        await _EnsureInitializedAsync();
        if (SelectedTab is not null)
            await SelectedTab.ReloadAsync();
    }

    [RelayCommand]
    private Task Refresh() => SelectedTab?.ReloadAsync() ?? Task.CompletedTask;

    /// <summary>Loads every local character's own client-only compositions into the Local tab.</summary>
    private async Task _LoadLocalTabAsync(CompositionTabViewModel tab)
    {
        tab.Loaded.Clear();
        var characters = await _characters.GetAllAsync();
        foreach (var character in characters.Where(c => c.EsiCharacterId is not null))
        {
            var ownerId = character.EsiCharacterId!.Value;
            var client = new LocalFleetCompositionClient(_localFleets, _compositionRepository, ownerId);
            foreach (var info in await client.ListAsync())
                tab.Loaded.Add(new CompositionRowViewModel(info, character.Name, canEdit: true, isLocal: true, client,
                    _services.GetService<ITypeImageProvider>()));
        }

        await Task.WhenAll(tab.Loaded.Select(row => row.LoadSummaryAsync()));
        tab.SetFilter(SearchText);
        tab.Status = tab.Loaded.Count == 0 ? "No local compositions yet — create one with “+ NEW COMPOSITION”." : "";
    }

    /// <summary>Loads a single server's shared compositions (every coupled character's own), guarded by the connection
    /// state so a disconnected server reads clearly instead of an empty list.</summary>
    private async Task _LoadServerTabAsync(CompositionTabViewModel tab)
    {
        tab.Loaded.Clear();
        var server = tab.ServerAddress!;

        if (_services.GetService<IRemoteBusConnector>()?.StateFor(server) != ServerConnectionState.Connected)
        {
            tab.SetFilter(SearchText);
            tab.Status = "Not connected — couple a character to this server first.";
            return;
        }

        var sessions = await _sessions.LoadAllAsync(server);
        if (sessions.Count == 0)
        {
            tab.SetFilter(SearchText);
            tab.Status = "Not connected — couple a character to this server first.";
            return;
        }

        // One server-wide list as the first coupled character; each row carries its own edit-state (owner-or-manage)
        // and the server-resolved owner name. In v1 the policy grants manage to everyone, so all are editable.
        var client = new ServerFleetCompositionClient(_transport, server, sessions[0].CharacterId);
        foreach (var info in await client.ListAllAsync())
            tab.Loaded.Add(new CompositionRowViewModel(info, info.OwnerName, canEdit: info.CanEdit, isLocal: false, client,
                _services.GetService<ITypeImageProvider>()));

        await Task.WhenAll(tab.Loaded.Select(row => row.LoadSummaryAsync()));
        tab.SetFilter(SearchText);
        tab.Status = tab.Loaded.Count == 0 ? "No compositions shared on this server yet." : "";
    }

    /// <summary>Creates a new client-only composition owned by <paramref name="ownerCharacterId"/> and reloads the
    /// Local tab. A thin seam over the editor flow, kept for tests.</summary>
    public async Task<bool> CreateLocalCompositionAsync(string name, int ownerCharacterId)
    {
        var client = new LocalFleetCompositionClient(_localFleets, _compositionRepository, ownerCharacterId);
        var (ok, message, _) = await client.CreateAsync(name, null);
        StatusMessage = ok ? $"Created \"{name}\"." : message;
        if (ok)
            await ReloadAsync();
        return ok;
    }

    [RelayCommand]
    private async Task NewComposition()
    {
        var tab = SelectedTab;
        if (tab is null)
            return;

        IFleetCompositionClient client;
        if (tab.IsLocal)
        {
            var owner = await _ResolveLocalOwnerAsync();
            if (owner is null)
            {
                await _dialogs.ShowMessageAsync("New composition", "Add a local character first.");
                return;
            }
            client = new LocalFleetCompositionClient(_localFleets, _compositionRepository, owner.Value);
        }
        else
        {
            var character = await _ResolveServerCharacterAsync(tab.ServerAddress!, "Create the composition as which character?");
            if (character is null)
                return;
            client = new ServerFleetCompositionClient(_transport, tab.ServerAddress!, character.Value);
        }

        var editor = CompositionEditorViewModel.ForNew(_services, client);
        if (await _dialogs.ShowCompositionEditorAsync(editor))
            await tab.ReloadAsync();
    }

    [RelayCommand]
    private async Task OpenComposition(CompositionRowViewModel? row)
    {
        if (row is null)
            return;

        var detail = await row.Client.GetAsync(row.Id);
        if (detail is null)
        {
            await _dialogs.ShowMessageAsync("Open composition", "Could not load this composition.");
            return;
        }

        // Own (or server-granted) compositions open in the editor; others open read-only — view + fit-detail doorklik
        // for everyone, no edit. A read-only view never persists, so no reload is needed on close.
        if (!row.CanEdit)
        {
            await _dialogs.ShowCompositionEditorAsync(CompositionEditorViewModel.ForView(_services, row.Client, detail));
            return;
        }

        var editor = CompositionEditorViewModel.ForExisting(_services, row.Client, detail);
        if (await _dialogs.ShowCompositionEditorAsync(editor) && SelectedTab is not null)
            await SelectedTab.ReloadAsync();
    }

    /// <summary>Duplicates a composition (its whole graph) into the same source under a unique "(copy)" name, owned by
    /// the acting owner/character — so anyone can take a copy of another player's doctrine and edit their own.</summary>
    [RelayCommand]
    private async Task DuplicateComposition(CompositionRowViewModel? row)
    {
        if (row is null)
            return;

        var source = await row.Client.GetAsync(row.Id);
        if (source is null)
        {
            await _dialogs.ShowMessageAsync("Duplicate composition", "Could not load this composition.");
            return;
        }

        var existing = (await row.Client.ListAsync()).Select(c => c.Name).ToList();
        var copyName = _UniqueCopyName(source.Composition.Name, existing);
        var (ok, message) = await _CopyGraphAsync(source, row.Client, copyName);
        StatusMessage = ok ? $"Duplicated as \"{copyName}\"." : message;
        if (ok && SelectedTab is not null)
            await SelectedTab.ReloadAsync();
    }

    /// <summary>"{name} (copy)", then "(copy 2)", "(copy 3)"… until it does not collide with an existing name.</summary>
    private static string _UniqueCopyName(string baseName, IReadOnlyCollection<string> existing)
    {
        bool Taken(string candidate) => existing.Any(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase));
        var name = $"{baseName} (copy)";
        for (var n = 2; Taken(name); n++)
            name = $"{baseName} (copy {n})";
        return name;
    }

    [RelayCommand]
    private async Task DeleteComposition(CompositionRowViewModel? row)
    {
        if (row is null || !row.CanEdit)
            return;

        if (!await _dialogs.ConfirmAsync("Delete composition", $"Delete \"{row.Name}\"? This cannot be undone.", okText: "Delete"))
            return;

        var (ok, message) = await row.Client.DeleteAsync(row.Id);
        StatusMessage = ok ? $"Deleted \"{row.Name}\"." : message;
        if (ok && SelectedTab is not null)
            await SelectedTab.ReloadAsync();
    }

    /// <summary>Pushes a local composition (its whole graph) to a coupled server as the chosen character — the
    /// composition analogue of sharing a fit. Recreates the graph through the server facade (the gRPC
    /// seam), then opens the target server's tab so the result is visible.</summary>
    [RelayCommand]
    private async Task PushComposition(CompositionRowViewModel? row)
    {
        if (row is null || !row.IsLocal)
            return;

        var source = await row.Client.GetAsync(row.Id);
        if (source is null)
        {
            await _dialogs.ShowMessageAsync("Push composition", "Could not load this composition.");
            return;
        }

        // Opsec/privacy gate: a composition carries a full self-contained copy of every fit (ship + modules)
        // — pushing it shares those fits with the server, where others can see them. Never share fits unprompted:
        // confirm the intent up front, before any fit leaves the machine. A fit-less doctrine shares nothing → no prompt.
        var sharedFits = source.Roles.SelectMany(r => r.Entries).Select(e => e.Fit.ContentHash).Distinct().Count();
        if (sharedFits > 0 && !await _dialogs.ConfirmAsync(
                "Share fits with a server?",
                $"\"{row.Name}\" contains {sharedFits} fit{(sharedFits == 1 ? "" : "s")}. Pushing it sends a full copy of " +
                "each fit (ship + modules) to the server, where anyone with access to that server can view them. " +
                "Only push doctrines whose fits you are comfortable sharing.",
                okText: "Push & share"))
        {
            StatusMessage = "Push cancelled — no fits were shared.";
            return;
        }

        var servers = await _sessions.ListServersAsync();
        if (servers.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Push composition", "Not coupled to any server — couple a character first.");
            return;
        }

        string targetAddress;
        if (servers.Count == 1)
        {
            targetAddress = servers[0];
        }
        else
        {
            var registry = _services.GetService<IServerRegistry>();
            var options = new List<ServerPickOption>();
            foreach (var address in servers)
                options.Add(new ServerPickOption(address, registry is null ? address : await registry.DisplayNameAsync(address)));
            var chosen = await _dialogs.SelectServerAsync($"Push \"{row.Name}\" to which server?", options);
            if (chosen is null)
                return;
            targetAddress = chosen;
        }

        if (_services.GetService<IRemoteBusConnector>()?.StateFor(targetAddress) != ServerConnectionState.Connected)
        {
            await _dialogs.ShowMessageAsync("Push composition", "Not connected to that server.");
            return;
        }

        var character = await _ResolveServerCharacterAsync(targetAddress, $"Push \"{row.Name}\" as which character?");
        if (character is null)
            return;

        var target = new ServerFleetCompositionClient(_transport, targetAddress, character.Value);
        var (ok, message) = await _CopyGraphAsync(source, target);
        StatusMessage = ok ? $"Pushed \"{row.Name}\" to the server." : message;

        if (ok)
            await _ShowServerTabAsync(targetAddress);
    }

    /// <summary>Downloads a server composition (its whole graph) into the local client-only library, owned by the
    /// chosen local character — the composition analogue of downloading a shared fit — then opens the Local tab.</summary>
    [RelayCommand]
    private async Task DownloadComposition(CompositionRowViewModel? row)
    {
        if (row is null || row.IsLocal)
            return;

        var source = await row.Client.GetAsync(row.Id);
        if (source is null)
        {
            await _dialogs.ShowMessageAsync("Download composition", "Could not load this composition.");
            return;
        }

        var owner = await _ResolveLocalOwnerAsync();
        if (owner is null)
        {
            await _dialogs.ShowMessageAsync("Download composition", "Add a local character first.");
            return;
        }

        var target = new LocalFleetCompositionClient(_localFleets, _compositionRepository, owner.Value);
        var (ok, message) = await _CopyGraphAsync(source, target);
        StatusMessage = ok ? $"Downloaded \"{row.Name}\" to the local library." : message;

        if (ok)
        {
            var local = Tabs.FirstOrDefault(t => t.IsLocal);
            if (local is not null)
            {
                await local.ReloadAsync();
                SelectedTab = local;
            }
        }
    }

    private async Task _ShowServerTabAsync(string serverAddress)
    {
        var tab = Tabs.FirstOrDefault(t => t.ServerAddress == serverAddress);
        if (tab is null)
            return;
        await tab.ReloadAsync();
        SelectedTab = tab;
    }

    /// <summary>Recreates a composition graph (header + role groups + fit entries) on a target facade, local or server.
    /// Skips the copy when the target already owns a composition with the same name (the dedup compositions have, since
    /// they carry no content hash).</summary>
    private static async Task<(bool Ok, string Message)> _CopyGraphAsync(
        FleetCompositionDetail source, IFleetCompositionClient target, string? targetName = null)
    {
        var name = targetName ?? source.Composition.Name;
        var existing = await target.ListAsync();
        if (existing.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return (false, $"\"{name}\" already exists there — not copied again.");

        var (created, message, compositionId) = await target.CreateAsync(name, source.Composition.Description);
        if (!created)
            return (false, message);

        foreach (var role in source.Roles)
        {
            var (roleOk, roleMessage, roleId) = await target.AddRoleAsync(compositionId, role.RoleName, role.GroupMinCount);
            if (!roleOk)
                return (false, roleMessage);

            foreach (var entry in role.Entries)
            {
                var (entryOk, entryMessage, _) = await target.AddEntryAsync(roleId, entry.Fit, entry.EntryMinCount);
                if (!entryOk)
                    return (false, entryMessage);
            }
        }

        return (true, "");
    }

    private async Task<int?> _ResolveLocalOwnerAsync()
    {
        var characters = (await _characters.GetAllAsync()).Where(c => c.EsiCharacterId is not null).ToList();
        return characters.Count switch
        {
            0 => null,
            1 => characters[0].EsiCharacterId,
            _ => await _dialogs.PickCharacterAsync(
                "Owner of the composition",
                characters.Select(c => new CharacterPickOption(c.EsiCharacterId!.Value, c.Name, "", Enabled: true)).ToList())
        };
    }

    private async Task<int?> _ResolveServerCharacterAsync(string serverAddress, string prompt)
    {
        var coupled = await _sessions.LoadAllAsync(serverAddress);
        return coupled.Count switch
        {
            0 => null,
            1 => coupled[0].CharacterId,
            _ => await _dialogs.PickCharacterAsync(prompt,
                coupled.Select(s => new CharacterPickOption(s.CharacterId, s.CharacterName, "coupled", Enabled: true)).ToList())
        };
    }
}
