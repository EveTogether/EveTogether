using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Transport;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Messaging.Dtos;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Events;
using EveUtils.Shared.Modules.Messaging.Repositories;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The message inbox: the durable local copy of delivered messages (mail + fleet invites). Subscribes
/// to <see cref="MessageDeliveredEvent"/> on the local bus (live push when online; the same event replays on
/// connect), writes each into the client SQLite inbox, and exposes the list + an unread badge for the main
/// window. Per-character copies of one action are merged into a single row while multiboxing; answering an
/// invite calls the generic RespondToMessage RPC, which delegates to the fleet server-side.
/// </summary>
public partial class InboxViewModel : ViewModelBase, ITransientService
{
    private readonly IClientInboxStore? _store;
    private readonly IFleetTransportClient? _fleetClient;
    private readonly IClientSessionStore? _sessionStore;
    private readonly ICharacterRegistry? _characters;
    private readonly IDialogService? _dialogs;
    private readonly IToastService? _toasts;
    private readonly IFleetMetricsLauncher? _metricsLauncher;
    private readonly DateTimeOffset _sessionStart;

    // One toast per logical action: multibox copies (same kind/server/title/body/sender, different recipient)
    // collapse to one, keyed exactly like the inbox row merge (see GroupByAction).
    private readonly HashSet<(MessageKind Kind, string? ServerAddress, string Title, string? Body, int? SenderCharacterId)> _toastedActions = [];

    public ObservableCollection<InboxItemViewModel> Messages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearAllCommand))]
    private bool _isEmpty = true;

    [ObservableProperty] private string _status = "";

    public bool HasUnread => UnreadCount > 0;

    /// <summary>Design-time / fallback (no services).</summary>
    public InboxViewModel()
    {
    }

    public InboxViewModel(
        IClientInboxStore store, IFleetTransportClient fleetClient, IClientSessionStore sessionStore,
        ICharacterRegistry characters, IDialogService dialogs, IEventBus eventBus,
        IToastService toasts, IFleetMetricsLauncher metricsLauncher, TimeProvider timeProvider)
    {
        _store = store;
        _fleetClient = fleetClient;
        _sessionStore = sessionStore;
        _characters = characters;
        _dialogs = dialogs;
        _toasts = toasts;
        _metricsLauncher = metricsLauncher;
        _sessionStart = timeProvider.GetUtcNow();

        eventBus.Subscribe<MessageDeliveredEvent>(OnMessageDelivered);

        _ = ReloadAsync();
    }

    private void OnMessageDelivered(MessageDeliveredEvent integrationEvent) =>
        Dispatcher.UIThread.Post(() => _ = StoreAndReloadAsync(integrationEvent.Data, integrationEvent.SourceServerAddress));

    private async Task StoreAndReloadAsync(MessageDeliveredPayload payload, string? serverAddress)
    {
        if (_store is null)
            return;

        var wasInserted = await _store.UpsertAsync(new ClientInboxMessage
        {
            ServerMessageId = payload.ServerMessageId,
            ServerAddress = serverAddress, // the server this was delivered from → the response target
            RecipientCharacterId = payload.RecipientCharacterId,
            SenderCharacterId = payload.SenderCharacterId,
            Kind = payload.Kind,
            RefId = payload.RefId,
            Title = payload.Title,
            Body = payload.Body,
            PayloadJson = payload.PayloadJson,
            CreatedAt = payload.CreatedAt,
            ReceivedAt = DateTimeOffset.UtcNow,
            Status = MessageStatus.Pending,
            IsRead = false
        });
        await ReloadAsync();

        // Only a first-time delivery raises a toast — the same delivery event re-fires on every reconnect (replay),
        // and a fleet action is delivered once per coupled character while multiboxing. Both are de-duplicated so the
        // toast pops once, app-wide, even with the inbox and fleet windows closed.
        if (wasInserted)
            _RaiseDeliveryToast(payload, serverAddress);
    }

    /// <summary>Surface a delivered fleet action as a toast: a fleet-start with an "Open metrics" button, or an
    /// invite with Accept/Decline that reuse the inbox response handler. Other mail stays silent in the inbox.</summary>
    private void _RaiseDeliveryToast(MessageDeliveredPayload payload, string? serverAddress)
    {
        if (_toasts is null)
            return;

        // Only surface a live arrival: a reconnect replays the still-pending invites, whose CreatedAt is in the past.
        // This also covers a re-delivery that arrives with a fresh ServerMessageId (so it slips past the store dedup).
        // The tolerance absorbs client/server clock skew on a genuinely fresh delivery.
        if (payload.CreatedAt < _sessionStart - TimeSpan.FromMinutes(2))
            return;

        var actionKey = (payload.Kind, serverAddress, payload.Title, payload.Body, payload.SenderCharacterId);
        if (!_toastedActions.Add(actionKey))
            return; // a multibox copy of this same action already raised the toast

        switch (payload.Kind)
        {
            case MessageKind.FleetStarted when _metricsLauncher is not null && serverAddress is { } server && payload.RefId is { } fleetId:
                _toasts.Show(payload.Title, payload.Body, ToastKind.Success,
                    [new ToastAction("Open metrics",
                        () => _ = _metricsLauncher.LaunchAsync(server, fleetId, payload.RecipientCharacterId))]);
                break;

            case MessageKind.FleetInvite when _FindPendingInvite(payload) is { } invite:
                _toasts.Show(payload.Title, payload.Body, ToastKind.Information,
                    [new ToastAction("Accept", () => _ = RespondAsync(invite, accept: true), ToastActionStyle.Affirmative),
                     new ToastAction("Decline", () => _ = RespondAsync(invite, accept: false), ToastActionStyle.Destructive)]);
                break;

            // The commander's "we have started — are you coming?" (ET-168). Both answers are on the toast, and
            // letting it fade is the third one: the request stays in the inbox while the fleet runs.
            case MessageKind.FleetSwitchRequest when _FindPendingInvite(payload) is { } request:
                _toasts.Show(payload.Title, payload.Body, ToastKind.Information,
                    [new ToastAction("Switch", () => _ = RespondAsync(request, accept: true), ToastActionStyle.Affirmative),
                     new ToastAction("Stay put", () => _ = RespondAsync(request, accept: false))]);
                break;
        }
    }

    /// <summary>The just-reloaded merged inbox row for this delivery, if it still has an answer outstanding.</summary>
    private InboxItemViewModel? _FindPendingInvite(MessageDeliveredPayload payload) =>
        Messages.FirstOrDefault(m => (m.IsInvite || m.IsSwitchRequest) && m.CanRespond
                                     && m.Title == payload.Title && m.Body == payload.Body);

    /// <summary>Called when the inbox window opens: mark everything read so the badge clears.</summary>
    public async Task OnOpenedAsync()
    {
        if (_store is null)
            return;

        foreach (var item in Messages.Where(m => !m.IsRead))
            foreach (var id in item.LocalIds)
                await _store.MarkReadAsync(id);
        await ReloadAsync();
    }

    public async Task RespondAsync(InboxItemViewModel item, bool accept)
    {
        if (_fleetClient is null || _sessionStore is null || _store is null)
            return;

        // answer each copy on the server it was delivered from. Fall back to the first coupled server only for
        // rows stored before the origin was tracked (null ServerAddress).
        var fallback = (await _sessionStore.ListServersAsync()).FirstOrDefault();
        var anySucceeded = false;
        string? lastError = null;

        foreach (var invite in item.PendingInvites)
        {
            var server = invite.ServerAddress ?? fallback;
            if (server is null)
            {
                Status = "Not paired with a server.";
                return;
            }

            // answer as the character that received the copy (the recipient), not the most-recent session.
            var result = await _fleetClient.RespondToMessageAsync(server, invite.ServerMessageId, accept, invite.RecipientCharacterId);

            // Accepting an invite while this character is still flying in another started fleet is refused, and the
            // refusal is right: the two are one character and only one of them can count (ET-168). What was wrong
            // was leaving the pilot with an error and two acts to work out for themselves. Switching is offered
            // here instead, with both steps named, and the invite is answered by the same act.
            if (!result.Ok && accept && item.IsInvite
                && await OfferSwitchInsteadAsync(server, invite) is true)
                result = (true, string.Empty);

            if (result.Ok)
            {
                await _store.SetStatusAsync(invite.Id, MessageStatus.Responded);
                anySucceeded = true;
            }
            else
            {
                lastError = result.Message;
            }
        }

        if (anySucceeded)
            Status = accept ? "Accepted." : "Declined.";
        else if (lastError is not null)
            Status = $"Failed: {lastError}";

        await ReloadAsync();
    }

    /// <summary>
    /// The way through a refused invite (ET-168, scherm 7). In the code, coming over is two acts — leave the fleet
    /// you count for, then couple here — and the second is refused outright while the first has not happened. This
    /// asks once, showing both steps, and then has the server do them as one; the invite is accepted as part of it,
    /// so the pilot never has to answer the same question twice.
    ///
    /// <para>Returns null when there was nothing to offer (no such invite, nothing to leave), false when the pilot
    /// backed out or the switch failed — in both of those the original refusal is what they are told.</para>
    /// </summary>
    private async Task<bool?> OfferSwitchInsteadAsync(string server, ClientInboxMessage copy)
    {
        if (_fleetClient is null || _dialogs is null || copy.RefId is not { } inviteId)
            return null;

        var invite = (await _fleetClient.ListPendingInvitesAsync(server, copy.RecipientCharacterId))
            .FirstOrDefault(i => i.Id == inviteId);
        if (invite is null)
            return null;

        var target = await _fleetClient.GetFleetAsync(server, invite.FleetId, copy.RecipientCharacterId);
        if (target is null)
            return null;

        // What this character counts for right now, on this server: the fleets a switch has to walk them out of.
        var leaving = (await _fleetClient.ListMyFleetsAsync(server, copy.RecipientCharacterId))
            .Where(f => f.Activation == FleetActivation.Active && f.Id != target.Id)
            .OrderBy(f => f.ActivatedAt ?? DateTimeOffset.MaxValue)
            .Select(f => new SwitchFleetLeaving(f.Name, f.ActivatedAt))
            .ToList();
        if (leaving.Count == 0)
            return null;   // the refusal was about something else; do not offer an act that would not have helped

        var name = _characters is null
            ? $"Char {copy.RecipientCharacterId}"
            : (await _characters.GetAllAsync()).FirstOrDefault(c => c.EsiCharacterId == copy.RecipientCharacterId)?.Name
              ?? $"Char {copy.RecipientCharacterId}";

        if (!await _dialogs.ConfirmFleetSwitchAsync(
                new SwitchFleetPrompt(name, target.Name, target.ActivatedAt, leaving, [])))
            return false;

        var switched = await _fleetClient.SwitchToFleetAsync(server, target.Id, copy.RecipientCharacterId);
        if (!switched.Ok)
            Status = $"Switch failed: {switched.Message}";
        return switched.Ok;
    }

    public async Task DeleteAsync(InboxItemViewModel item)
    {
        if (_store is null)
            return;

        foreach (var id in item.LocalIds)
            await _store.DeleteAsync(id);
        Status = "Message deleted.";
        await ReloadAsync();
    }

    private bool CanClearAll() => !IsEmpty;

    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private async Task ClearAll()
    {
        if (_store is null || IsEmpty)
            return;

        if (_dialogs is not null)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Clear inbox",
                "Delete all messages from your inbox? This cannot be undone.",
                okText: "Clear");
            if (!confirmed)
                return;
        }

        await _store.DeleteAllAsync();
        Status = "Inbox cleared.";
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_store is null)
            return;

        // Multi-character: the inbox aggregates messages for all my coupled characters. Resolve recipient
        // ids → names from the local character registry so each merged row can list which of my characters it hit.
        var names = _characters is null
            ? new Dictionary<int, string>()
            : (await _characters.GetAllAsync())
                .Where(c => c.EsiCharacterId is not null)
                .ToDictionary(c => c.EsiCharacterId!.Value, c => c.Name);

        var all = await _store.ListAllAsync();
        Messages.Clear();
        foreach (var group in GroupByAction(all))
        {
            var recipientNames = group
                .Select(m => names.TryGetValue(m.RecipientCharacterId, out var name) ? name : $"Char {m.RecipientCharacterId}")
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Messages.Add(new InboxItemViewModel(this, group, recipientNames));
        }

        IsEmpty = Messages.Count == 0;
        UnreadCount = Messages.Count(m => !m.IsRead);
    }

    /// <summary>Merge the per-character copies of one action (same kind/server/title/body/sender) into a single group,
    /// preserving the newest-first order of the first copy seen.</summary>
    private static IEnumerable<IReadOnlyList<ClientInboxMessage>> GroupByAction(IReadOnlyList<ClientInboxMessage> all)
    {
        var groups = new List<List<ClientInboxMessage>>();
        var index = new Dictionary<(MessageKind, string?, string, string?, int?), int>();
        foreach (var message in all)
        {
            var key = (message.Kind, message.ServerAddress, message.Title, message.Body, message.SenderCharacterId);
            if (index.TryGetValue(key, out var i))
            {
                groups[i].Add(message);
            }
            else
            {
                index[key] = groups.Count;
                groups.Add([message]);
            }
        }
        return groups;
    }
}
