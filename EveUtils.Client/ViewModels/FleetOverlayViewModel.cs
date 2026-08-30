using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The fleet pop-out (ET-72): the handful of things an FC actually acts on in a fight, in one borderless window you
/// lay over EVE. Not a small second fleet-metrics screen — this is read in the seconds when there is no time to read,
/// so everything in it has to earn its line.
///
/// Three things, and they were chosen by asking what decision each one produces:
/// <list type="bullet">
/// <item><b>Is the fleet together</b> — the WITH FC ratio, straight off <see cref="IFleetOverlaySource"/>, so it is
/// literally the same badge the screen shows. The decision: hold, or call people in.</item>
/// <item><b>Who is taking the most damage</b> — the pilot about to die. The decision: rep them, or tell them to warp.</item>
/// <item><b>Who is being neuted the most</b> — the pilot about to be able to do nothing. The decision: get them out,
/// or kill the neuting ship.</item>
/// </list>
///
/// Nothing else was added. Fleet DPS out, member counts and alpha are all worth knowing and none of them is worth
/// slowing down the three lines above, which is the whole trade this window makes.
///
/// It reads; it never subscribes. Every figure comes from the member rows the fleet-metrics screen already keeps up
/// to date, so the overlay cannot end up telling a different story than the screen that opened it — the failure this
/// project has now had nine times.
/// </summary>
public sealed partial class FleetOverlayViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the readout is worked out again. Four times a second is faster than an FC can act and slow
    /// enough that no figure is ever mid-flicker; the underlying rates are already smoothed at 30fps.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A member whose last sample is older than this is not reporting, so their figures are frozen wherever they
    /// happened to stop. This is NOT a second reading of "offline" — that judgement is <see cref="DpsViewModel.IsOffline"/>
    /// and it is the other half of the same test below. This asks something else: is the number in front of me
    /// current? Nothing publishes a zero on a pilot's behalf when their client disappears mid-fight, so without this
    /// the window would name whoever was worst off at the moment they dropped, and go on naming them for the rest of
    /// the evening. Samples arrive at 1 Hz.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    // Below these, there is nothing to act on: a stray drone hit, one neut cycle at the edge of range. The floors are
    // what make the quiet state quiet — see FleetSpotlight for the two rules that keep it steady above them.
    private const long IncomingDamageFloor = 25;   // dps
    private const long IncomingNeutFloor = 5;      // GJ/s — roughly one medium neutraliser cycling

    private static readonly TimeSpan SwitchAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(3);
    private const double SwitchMargin = 1.25;

    private readonly IFleetOverlaySource _source;
    private readonly FleetSpotlight _incoming = new(IncomingDamageFloor, SwitchMargin, SwitchAfter, QuietAfter);
    private readonly FleetSpotlight _neuted = new(IncomingNeutFloor, SwitchMargin, SwitchAfter, QuietAfter);
    private DispatcherTimer? _timer;

    public FleetOverlayViewModel(IFleetOverlaySource source)
    {
        _source = source;
        FleetName = source.FleetName;
        FleetId = source.FleetId;
        CommanderPresence = source.CommanderPresence;
    }

    /// <summary>The fleet this overlay is about. Shown in the header and in the window title, because "which fleet"
    /// is not a question you want to be asking of a window you put on top of your game.</summary>
    public string FleetName { get; }

    /// <summary>The fleet whose overlay this is — what its remembered position and size are keyed on.</summary>
    public long FleetId { get; }

    /// <summary>The WITH FC ratio, as the fleet-metrics header shows it (ET-31/ET-63/ET-71). Offline and unknown
    /// members are already accounted for there and are not counted again here.</summary>
    [ObservableProperty] private FleetCommanderPresence _commanderPresence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuiet))]
    private bool _hasIncoming;

    [ObservableProperty] private string _incomingName = EmptyName;
    [ObservableProperty] private string _incomingValue = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuiet))]
    private bool _hasNeuted;

    [ObservableProperty] private string _neutedName = EmptyName;
    [ObservableProperty] private string _neutedValue = string.Empty;

    /// <summary>
    /// Nothing is happening to anybody. The window keeps its size and its two slots — a window that resizes itself
    /// while you are flying is its own kind of noise — but everything in it goes grey, so "all quiet" is a thing you
    /// see from the corner of your eye rather than a thing you read. A window that looks the same busy and idle is a
    /// window you learn to ignore.
    /// </summary>
    public bool IsQuiet => !HasIncoming && !HasNeuted;

    private const string EmptyName = "—";

    /// <summary>Begin refreshing. Separate from the constructor so a test drives <see cref="Refresh"/> with a clock
    /// it controls instead of racing a timer.</summary>
    public void Start()
    {
        if (_timer is not null)
            return;

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh(DateTime.UtcNow);
        _timer.Start();
        Refresh(DateTime.UtcNow);
    }

    /// <summary>
    /// Work the readout out again from the member rows as they stand. Public and clock-driven so it is the same code
    /// under a test as under the timer.
    /// </summary>
    public void Refresh(DateTime nowUtc)
    {
        CommanderPresence = _source.CommanderPresence;

        var reporting = _source.Members.Where(m => IsReporting(m, nowUtc)).ToList();

        _incoming.Update(reporting.Select(m => new FleetSpotlight.Candidate(m.Character, m.Received)).ToList(), nowUtc);
        _neuted.Update(reporting.Select(m => new FleetSpotlight.Candidate(m.Character, m.NeutIn)).ToList(), nowUtc);

        HasIncoming = _incoming.HasSubject;
        IncomingName = _incoming.Name ?? EmptyName;
        IncomingValue = _incoming.HasSubject ? Format(_incoming.Value, "dps") : string.Empty;

        HasNeuted = _neuted.HasSubject;
        NeutedName = _neuted.Name ?? EmptyName;
        NeutedValue = _neuted.HasSubject ? Format(_neuted.Value, "GJ/s") : string.Empty;
    }

    /// <summary>
    /// Whether this member's figures may be named at all. Two halves, and they answer different questions:
    /// <see cref="DpsViewModel.IsOffline"/> — the one verdict ET-71 left behind, reused rather than restated — says
    /// we know the pilot is not in game, and <see cref="DpsViewModel.LastSampleAt"/> says whether what we see is still
    /// arriving. A pilot fails either and they are not named; naming someone off a frozen number is the same lie the
    /// logged-off pilot's last known system was.
    /// </summary>
    private static bool IsReporting(DpsViewModel member, DateTime nowUtc) =>
        !member.IsOffline
        && !string.IsNullOrWhiteSpace(member.Character)
        && member.LastSampleAt is { } at
        && nowUtc - at.UtcDateTime <= StaleAfter;

    private static string Format(long value, string unit) =>
        value.ToString(CultureInfo.InvariantCulture) + " " + unit;

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}
