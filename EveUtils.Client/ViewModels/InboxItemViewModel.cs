using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One row in the inbox: a mail (title + body), or something that can be answered — a fleet invite, or a fleet
/// commander asking a pilot who is flying elsewhere to come over (ET-168). When the same action is delivered to
/// several of my coupled characters while multiboxing, those per-character copies are merged into a single row: the
/// recipients are listed in the "To:" label and a response or delete fans out to every underlying copy, each
/// answered on its own origin server.
///
/// <para>The two answerable kinds do not read alike, so the buttons do not either. An invite is Accept/Decline. A
/// switch request is <b>Switch</b> or <b>Stay where I am</b> — and the third answer, "later", is no button at all:
/// leaving the message alone <i>is</i> the answer, and it keeps standing for as long as the fleet runs.</para>
/// </summary>
public partial class InboxItemViewModel : ObservableObject
{
    private readonly InboxViewModel _owner;
    private readonly IReadOnlyList<ClientInboxMessage> _messages;

    /// <summary>The merged per-character copies behind this row (one per recipient) — the delete fan-out targets.</summary>
    public IReadOnlyList<long> LocalIds { get; }

    public string Title { get; }
    public string? Body { get; }
    public bool IsInvite { get; }

    /// <summary>A fleet commander asking this pilot to leave the fleet they count for and come here (ET-168).</summary>
    public bool IsSwitchRequest { get; }

    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    /// <summary>Yes reads as what it does. "Accept" for an invite; for a switch request it is a move, and the pilot
    /// should see that before pressing rather than after.</summary>
    public string AcceptLabel => IsSwitchRequest ? "Switch to this fleet" : "Accept";

    /// <summary>No is not leaving: declining a switch keeps the pilot on that fleet's roster, merely not linked, so
    /// next week it is still there with them on it.</summary>
    public string DeclineLabel => IsSwitchRequest ? "No, I'll stay where I am" : "Decline";

    /// <summary>The third answer to a switch request, and the only one with no button: doing nothing. Said out
    /// loud, because a row with two buttons reads as a question that has to be answered now.</summary>
    public string? LaterHint => IsSwitchRequest
        ? "Or leave it: the request keeps standing while the fleet runs, and switching an hour from now still counts."
        : null;

    public bool HasLaterHint => LaterHint is not null;

    /// <summary>"To: A, B, C" — every one of my characters this action was addressed to.</summary>
    public string RecipientLabel { get; }

    /// <summary>When the message was sent, in local time.</summary>
    public string TimestampLabel { get; }

    [ObservableProperty] private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRespond))]
    private MessageStatus _status;

    /// <summary>An answerable kind can be answered while still pending; mail and answered ones cannot.</summary>
    public bool CanRespond => (IsInvite || IsSwitchRequest) && Status == MessageStatus.Pending;

    public InboxItemViewModel(InboxViewModel owner, IReadOnlyList<ClientInboxMessage> messages, IReadOnlyList<string> recipientNames)
    {
        _owner = owner;
        _messages = messages;
        var head = messages[0]; // newest copy of the merged action (the list arrives newest-first)
        LocalIds = messages.Select(m => m.Id).ToArray();
        Title = head.Title;
        Body = head.Body;
        IsInvite = head.Kind == MessageKind.FleetInvite;
        IsSwitchRequest = head.Kind == MessageKind.FleetSwitchRequest;
        RecipientLabel = "To: " + string.Join(", ", recipientNames);
        TimestampLabel = FormatTimestamp(messages.Max(m => m.CreatedAt));
        _isRead = messages.All(m => m.IsRead);
        // A part-answered group still offers actions: stay Pending while any copy is unanswered.
        _status = messages.Any(m => m.Status == MessageStatus.Pending) ? MessageStatus.Pending : head.Status;
    }

    /// <summary>The copies that still need an answer — each replied to on its own origin server. Both answerable
    /// kinds, because a switch request is delivered per coupled character exactly as an invite is.</summary>
    internal IReadOnlyList<ClientInboxMessage> PendingInvites =>
        _messages.Where(m => m.Kind is MessageKind.FleetInvite or MessageKind.FleetSwitchRequest
                             && m.Status == MessageStatus.Pending).ToArray();

    private static string FormatTimestamp(DateTimeOffset createdAtUtc)
    {
        var local = createdAtUtc.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return $"Today {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday {local:HH:mm}";
        return local.Year == today.Year ? local.ToString("dd MMM, HH:mm") : local.ToString("dd MMM yyyy, HH:mm");
    }

    [RelayCommand] private Task Accept() => _owner.RespondAsync(this, true);

    [RelayCommand] private Task Decline() => _owner.RespondAsync(this, false);

    [RelayCommand] private Task Delete() => _owner.DeleteAsync(this);
}
