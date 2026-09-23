using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Messaging.Entities;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>One line of the home's activity: the same title on the same day is one line with a count.</summary>
public sealed record HomeActivityLine(MaterialIconKind Icon, string Title, string CountText, string DateText, bool IsUnread)
{
    public bool HasCount => CountText.Length > 0;
}

/// <summary>
/// ACTIVITY (ET-324): the four newest things that happened, from the inbox the shell already keeps — nothing read here.
/// Identical titles on one day collapse into one line ("×3"); the unread count heads it and the inbox has the rest.
/// </summary>
public sealed partial class HomeActivityViewModel : ObservableObject, IDisposable
{
    public const int Shown = 4;

    private readonly InboxViewModel? _inbox;
    private readonly HomeNavigation _navigation;
    private bool _isShowPosted;

    public HomeActivityViewModel(InboxViewModel? inbox, HomeNavigation navigation)
    {
        _inbox = inbox;
        _navigation = navigation;
        if (_inbox is null)
            return;

        _inbox.Messages.CollectionChanged += _OnMessagesChanged;
        _inbox.PropertyChanged += _OnInboxChanged;
        _Show();
    }

    public ObservableCollection<HomeActivityLine> Lines { get; } = [];

    [ObservableProperty] private bool _hasLines;
    [ObservableProperty] private string _readText = string.Empty;
    [ObservableProperty] private bool _hasUnread;

    [RelayCommand]
    private void OpenInbox() => _navigation.LaunchModule("inbox");

    /// <summary>The inbox rebuilds its list with a Clear and an Add per message: one redraw after the last of them.</summary>
    private void _OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isShowPosted)
            return;

        _isShowPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isShowPosted = false;
            _Show();
        }, DispatcherPriority.Background);
    }

    private void _OnInboxChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InboxViewModel.UnreadCount))
            _Show();
    }

    private void _Show()
    {
        if (_inbox is null)
            return;

        HomeActivityLine[] lines = [.. _LinesOf(_inbox.Messages)];
        Lines.ReconcileTo([.. lines.Select(line => Lines.FirstOrDefault(shown => shown == line) ?? line)]);
        HasLines = lines.Length > 0;
        _ShowReadText();
    }

    /// <summary>Newest first; a title repeated on the same local day is one line with how many times it came.</summary>
    private static IEnumerable<HomeActivityLine> _LinesOf(IEnumerable<InboxItemViewModel> messages) =>
        messages
            .GroupBy(message => (message.Title, Day: DateOnly.FromDateTime(message.CreatedAt.ToLocalTime().DateTime)))
            .Select(group => (Newest: group.OrderByDescending(message => message.CreatedAt).First(), Count: group.Count(),
                IsUnread: group.Any(message => !message.IsRead)))
            .OrderByDescending(entry => entry.Newest.CreatedAt)
            .Take(Shown)
            .Select(entry => new HomeActivityLine(
                _IconOf(entry.Newest.Kind),
                entry.Newest.Title,
                entry.Count > 1 ? "×" + entry.Count.ToString(CultureInfo.InvariantCulture) : string.Empty,
                entry.Newest.CreatedAt.ToLocalTime().ToString("ddd d MMM", CultureInfo.InvariantCulture),
                entry.IsUnread));

    private void _ShowReadText()
    {
        int unread = _inbox?.UnreadCount ?? 0;
        HasUnread = unread > 0;
        ReadText = HasUnread ? $"{unread} unread" : "all read";
    }

    private static MaterialIconKind _IconOf(MessageKind kind) => kind switch
    {
        MessageKind.Mail => MaterialIconKind.EmailOutline,
        MessageKind.FleetInvite or MessageKind.FleetJoinRequest => MaterialIconKind.AccountPlusOutline,
        _ => MaterialIconKind.AccountGroupOutline
    };

    public void Dispose()
    {
        if (_inbox is null)
            return;

        _inbox.Messages.CollectionChanged -= _OnMessagesChanged;
        _inbox.PropertyChanged -= _OnInboxChanged;
    }
}
