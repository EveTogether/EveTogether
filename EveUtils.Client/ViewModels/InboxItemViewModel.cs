using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One row in the inbox: a mail (title + body) or a fleet invite (title + Accept/Decline). When the same
/// action is delivered to several of my coupled characters while multiboxing, those per-character copies are merged
/// into a single row: the recipients are listed in the "To:" label and a response or delete fans out to every
/// underlying copy, each answered on its own origin server.
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
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    /// <summary>"To: A, B, C" — every one of my characters this action was addressed to.</summary>
    public string RecipientLabel { get; }

    /// <summary>When the message was sent, in local time.</summary>
    public string TimestampLabel { get; }

    [ObservableProperty] private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRespond))]
    private MessageStatus _status;

    /// <summary>An invite can be answered while still pending; mail and answered invites cannot.</summary>
    public bool CanRespond => IsInvite && Status == MessageStatus.Pending;

    public InboxItemViewModel(InboxViewModel owner, IReadOnlyList<ClientInboxMessage> messages, IReadOnlyList<string> recipientNames)
    {
        _owner = owner;
        _messages = messages;
        var head = messages[0]; // newest copy of the merged action (the list arrives newest-first)
        LocalIds = messages.Select(m => m.Id).ToArray();
        Title = head.Title;
        Body = head.Body;
        IsInvite = head.Kind == MessageKind.FleetInvite;
        RecipientLabel = "To: " + string.Join(", ", recipientNames);
        TimestampLabel = FormatTimestamp(messages.Max(m => m.CreatedAt));
        _isRead = messages.All(m => m.IsRead);
        // A part-answered group still offers actions: stay Pending while any copy is unanswered.
        _status = messages.Any(m => m.Status == MessageStatus.Pending) ? MessageStatus.Pending : head.Status;
    }

    /// <summary>The pending invite copies that still need an answer — each replied to on its own origin server.</summary>
    internal IReadOnlyList<ClientInboxMessage> PendingInvites =>
        _messages.Where(m => m.Kind == MessageKind.FleetInvite && m.Status == MessageStatus.Pending).ToArray();

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
