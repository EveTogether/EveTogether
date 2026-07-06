using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Messaging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The per-character settings dialog: identity + ESI status + ESI scopes (+ add-scope
/// re-auth) + the list of coupled servers (each with live status, a gear-button trust dialog and Decouple) +
/// "Couple to server". Opened from the gear button on a character row; replaces the former always-on detail pane.
/// Delegates the actual operations to <see cref="MainWindowViewModel"/> (one source of truth for the
/// couple/decouple/re-auth flows) and rebuilds its own view of the data afterwards.
/// </summary>
public partial class CharacterDialogViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly CharacterViewModel _initial;

    public int CharacterId { get; }

    /// <summary>No ESI link — surfaced from a gamelog only.</summary>
    public bool IsLocalOnly { get; }

    /// <summary>Server-coupling + ESI scopes are only offered for ESI-linked characters.</summary>
    public bool CanCouple => !IsLocalOnly;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _esiStatus = "";
    [ObservableProperty] private string _status = "";

    /// <summary>This character's coupled servers, each with its own live state, a gear button and Decouple.</summary>
    public ObservableCollection<ServerLinkViewModel> ServerLinks { get; } = [];

    public CharacterDialogViewModel(MainWindowViewModel owner, CharacterViewModel character)
    {
        _owner = owner;
        _initial = character;
        CharacterId = character.CharacterId;
        IsLocalOnly = character.IsLocalOnly;
        if (_owner.Bus is not null)
            _owner.Bus.StateChanged += OnServerState;
    }

    /// <summary>Loads the character snapshot + its coupled-server links. Call right after construction.</summary>
    public async Task InitializeAsync()
    {
        ApplyCharacterSnapshot();
        await ReloadServerLinksAsync();
    }

    private void ApplyCharacterSnapshot()
    {
        // ESI characters are re-read fresh from the (rebuilt) list by id; local-only rows share id 0, so fall
        // back to the snapshot we were opened with (they have no scope/couple state to refresh anyway).
        var c = IsLocalOnly ? _initial : _owner.Characters.FirstOrDefault(x => x.CharacterId == CharacterId) ?? _initial;
        Name = c.Name;
        EsiStatus = c.EsiStatus;
    }

    private async Task ReloadServerLinksAsync()
    {
        var links = await _owner.BuildServerLinksAsync(CharacterId, DecoupleAsync, ViewTrustAsync);
        ServerLinks.Clear();
        foreach (var link in links)
            ServerLinks.Add(link);
    }

    [RelayCommand]
    private async Task ReAuthenticate()
    {
        Status = "Opening scope selection…";
        await _owner.ReAuthenticateAsync(CharacterId);
        ApplyCharacterSnapshot(); // owner refreshed the registry → reflect any scope change
        Status = "";
    }

    [RelayCommand]
    private async Task Couple()
    {
        if (IsLocalOnly)
        {
            Status = "Sign in with ESI first — server-coupling requires an ESI link.";
            return;
        }

        var ok = await _owner.RunCoupleAsync();
        if (ok)
        {
            ApplyCharacterSnapshot();
            await ReloadServerLinksAsync();
            Status = "Coupled.";
        }
        else
        {
            Status = "Coupling cancelled.";
        }
    }

    private async Task DecoupleAsync(ServerLinkViewModel link)
    {
        await _owner.DecoupleAsync(link);
        await ReloadServerLinksAsync();
    }

    private async Task ViewTrustAsync(ServerLinkViewModel link)
    {
        if (await _owner.ShowServerTrustAsync(link))
            await DecoupleAsync(link); // user pressed Decouple inside the trust dialog
    }

    private void OnServerState(string address, ServerConnectionState state) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var link in ServerLinks)
                if (string.Equals(link.Address, address, StringComparison.OrdinalIgnoreCase))
                    link.State = state;
        });

    public void Dispose()
    {
        if (_owner.Bus is not null)
            _owner.Bus.StateChanged -= OnServerState;
    }
}
