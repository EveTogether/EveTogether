using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Runs;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.ViewModels;

/// <summary>What the settings change about sharing with one server (ET-515): the pilot's new answer, and whether the
/// earlier runs go along with it.</summary>
public sealed record OutcomeSharingChange(string ServerAddress, OutcomeSharingChoice Choice, bool Backfill);

/// <summary>
/// The EARNINGS STATS block of Privacy &amp; Sharing (ET-515): one switch per coupled server, what is shared with it so
/// far, and why it is not going through when it is not. Nothing is applied until SAVE, like the rest of the dialog;
/// the sharing itself then happens in the background (<see cref="RunOutcomePublisher"/>).
/// </summary>
public sealed class OutcomeSharingSettingsViewModel : ObservableObject
{
    public const string Description =
        "Share only the outcome of your site runs with everyone on a server, so the expected earnings are based on all "
        + "pilots — not just you. Your own runs always count locally. Off by default; asked once per server.";

    public const string Anonymity =
        "Other pilots never see who a run came from. Turning this off removes everything you shared from that server.";

    public const string SeparateFromWorkbench =
        "This is separate from publishing to EVE Workbench, which shares full runs with EVE Workbench.";

    public const string NoServers = "Couple a server first (Servers) to share run outcomes with it.";

    private OutcomeSharingSettingsViewModel(IReadOnlyList<OutcomeSharingServerViewModel> servers)
    {
        Servers = new ObservableCollection<OutcomeSharingServerViewModel>(servers);
    }

    public ObservableCollection<OutcomeSharingServerViewModel> Servers { get; }

    public bool HasServers => Servers.Count > 0;

    public bool HasNoServers => !HasServers;

    /// <summary>The servers whose answer differs from the one the dialog opened with.</summary>
    public IReadOnlyList<OutcomeSharingChange> Changes() =>
        [.. Servers.Where(server => server.IsChanged).Select(server => server.ToChange())];

    public static async Task<OutcomeSharingSettingsViewModel> LoadAsync(RunOutcomePublisher publisher, IClientSessionStore sessions,
        IServerRegistry registry, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        List<OutcomeSharingServerViewModel> servers = [];
        foreach (string address in (await sessions.ListServersAsync(cancellationToken)).Order(StringComparer.Ordinal))
            servers.Add(new OutcomeSharingServerViewModel(publisher, address,
                await registry.DisplayNameAsync(address, cancellationToken),
                await publisher.StatusAsync(address, cancellationToken), nowUtc));
        return new OutcomeSharingSettingsViewModel(servers);
    }
}

public sealed partial class OutcomeSharingServerViewModel : ObservableObject
{
    private readonly RunOutcomePublisher _publisher;
    private readonly OutcomeSharingChoice _initialChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersBackfill))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isShared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersBackfill))]
    [NotifyPropertyChangedFor(nameof(BackfillText))]
    private int _backfillCount;

    [ObservableProperty] private bool _backfill = true;

    public OutcomeSharingServerViewModel(RunOutcomePublisher publisher, string address, string name, OutcomeSharingStatus status,
        DateTime nowUtc)
    {
        _publisher = publisher;
        Address = address;
        Name = name;
        _initialChoice = status.Choice;
        _isShared = status.Choice is OutcomeSharingChoice.Shared;
        SharedText = SharedLine(status, nowUtc);
        Problem = status.Problem;
    }

    public string Address { get; }

    public string Name { get; }

    public string? Problem { get; }

    public bool HasProblem => Problem is not null;

    public string SharedText { get; }

    /// <summary>The line under the switch: what is shared so far, or what ticking it will do.</summary>
    public string StatusText => IsShared == (_initialChoice is OutcomeSharingChoice.Shared)
        ? SharedText
        : IsShared ? "Sharing starts when you save." : "Everything you shared is removed when you save.";

    public bool IsChanged => IsShared != (_initialChoice is OutcomeSharingChoice.Shared);

    /// <summary>Only for a switch just turned on, and only when there are earlier runs to add.</summary>
    public bool OffersBackfill => IsShared && IsChanged && BackfillCount > 0;

    public string BackfillText =>
        $"Also share my runs from the last {RunOutcomePublisher.BackfillDays} days ({BackfillCount.ToString(CultureInfo.InvariantCulture)})";

    public OutcomeSharingChange ToChange() => new(Address, IsShared ? OutcomeSharingChoice.Shared : OutcomeSharingChoice.NotShared,
        Backfill && OffersBackfill);

    partial void OnIsSharedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsChanged));
        if (value && IsChanged && BackfillCount == 0)
            _ = _LoadBackfillCountAsync();
    }

    // Read when the switch is turned on, not when the dialog opens: it walks the runs of the last 90 days.
    private async Task _LoadBackfillCountAsync() => BackfillCount = await _publisher.BackfillCountAsync(Address);

    internal static string SharedLine(OutcomeSharingStatus status, DateTime nowUtc)
    {
        if (status.Choice is not OutcomeSharingChoice.Shared)
            return "Not shared";

        string count = $"{status.SharedCount.ToString(CultureInfo.InvariantCulture)} {(status.SharedCount == 1 ? "outcome" : "outcomes")}";
        string last = status.LastSharedAtUtc is { } at ? $" · last {Age(nowUtc - at)}" : string.Empty;
        string waiting = status.PendingCount > 0 ? $" · {status.PendingCount.ToString(CultureInfo.InvariantCulture)} waiting" : string.Empty;
        return $"Shared: {count}{last}{waiting}";
    }

    internal static string Age(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} d ago"
    };
}
