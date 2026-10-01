using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-417: with OPSEC on, no location is readable anywhere in the client's windows. Every place that shows one has to
/// say how it is handled, in the style of <see cref="CommandSignalCoverageTests"/>: masked (with the test that proves
/// it), exempt (with the reason it is not a location after all), or a known gap (with the ticket closing it).
///
/// Two sources of places. The scan reads every view's bindings and catches any property whose name says location —
/// a new screen binding <c>SystemName</c> fails here until it is decided. The audit list holds what the scan cannot
/// recognise by name: composite strings ("site · system") behind a generic name, toasts, menu headers and log lines,
/// each keyed on the source file and the member that builds the text.
/// </summary>
public sealed class OpsecCoverageTests
{
    private const string PlannedScreens = "ET-417 PR 2: screens, toasts and log views";
    private const string PlannedMap = "ET-417 PR 3: the map is hidden whole while OPSEC is on";

    private static readonly Regex BindingPath =
        new(@"\{(?:Binding|CompiledBinding|ReflectionBinding)\s+(?:Path=)?([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);

    private static readonly Regex LocationName = new(
        "(System|Site|Station|Structure|Region|Constellation|Signature|Security|Location|Jumps|Route|Escalation|Destination|Pocket|Dungeon)",
        RegexOptions.Compiled);

    // Flags, commands, brushes and sizes carry no text, whatever their name says.
    private static readonly Regex NotText = new(
        "^(Is|Has|Can|Show)[A-Z]|(Command|Brush|Color|Visible|Visibility|Width|Height|Opacity|Count|Index|Icon|Kind|Mode|Id)$",
        RegexOptions.Compiled);

    /// <summary>Location text the scan cannot recognise by name, from the ET-417 audit: "file|member that builds it".</summary>
    private static readonly string[] Audited =
    [
        "ViewModels/Runs/RunsActivityPaneViewModel.cs|MetaText",
        "ViewModels/Runs/RunningGroupViewModel.cs|KindText",
        "ViewModels/Home/HomeDashboardViewModel.cs|IdleText",
        "ViewModels/Runs/UnfinishedRunViewModel.cs|TitleText",
        "ViewModels/Runs/Sections/ActivityDetailSectionViewModel.cs|HeaderSummary",
        "ViewModels/Runs/Sections/ActivityWindowSectionViewModel.cs|HeaderSummary",
        "ViewModels/Activity/ActivityWindowViewModel.cs|ClockHint",
        "ViewModels/Activity/ActivityWindowViewModel.cs|_RowFor(sample.CharacterId).LocationText",
        "ViewModels/Runs/LinkedLossViewModel.cs|LinkedLossRunChoice",
        "ViewModels/Killmails/KillmailLinkedRunViewModel.cs|SiteText",
        "ViewModels/Home/HomePilotRowViewModel.cs|SystemDetailText",
        "ViewModels/FleetMemberMenu.cs|_LocationLine",
        "Fleet/FleetCommanderPresence.cs|Tooltip",
        "Fleet/FleetRunsInProgress.cs|Describe",
        "ViewModels/GameLogs/GameLogRowViewModel.cs|Text",
        "ViewModels/Runs/RunPublisher.cs|Publish '",
        "Runs/FleetRunWindowPresenter.cs|_Where",
        "Runs/HomefrontDetector.cs|Could not offer a homefront run on {Site}",
        "Runs/StartupResumeNoticeService.cs|Stage",
        "Clipboard/ClipboardSignatureOffer.cs|DescribeSignature",
        "ViewModels/Activity/ActivityWindowViewModel.cs|Copied signature {Signature}",
        "ViewModels/Map/MapFleetSystemChip.cs|SystemName",
    ];

    /// <summary>Places proven masked, each with the test (Class.Method) that proves it.</summary>
    private static readonly IReadOnlyDictionary<string, string> Masked = new Dictionary<string, string>();

    /// <summary>Scan hits that are no location at all, each with the reason. A claim a reviewer has to agree with.</summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>
    {
        ["Views/FitDetailWindow.axaml|Signature"] = "a ship's signature radius, a fitting stat, not a scanned signature",
        ["Views/FleetRosterWindow.axaml|EsiAutoApplyStructure"] = "whether the in-game wing and squad structure is applied, a fleet setting",
        ["Views/EscalationDialogWindow.axaml|SiteQuery"] = "the text the pilot is typing into a search box: masking an input makes it unusable",
        ["Views/ManualRunStartWindow.axaml|SiteQuery"] = "the text the pilot is typing into a search box: masking an input makes it unusable",
        ["Views/ManualRunStartWindow.axaml|AsksSiteGroup"] = "a flag that switches part of the form on, no text",
        ["Views/ManualRunStartWindow.axaml|NeedsSite"] = "a flag that switches part of the form on, no text",
        ["Views/ManualRunStartWindow.axaml|SiteGroups"] = "the fixed list of site categories, the same for every pilot anywhere",
        ["Views/ManualRunStartWindow.axaml|SelectedSiteGroup"] = "one of the fixed site categories, the same for every pilot anywhere",
        ["Views/Home/HomeDashboardView.axaml|SystemStrip"] = "the app and server status strip: \"system\" as in software, not a solar system",
        ["Views/Runs/Sections/MiningWindowSectionView.axaml|SiteRemainingFraction"] = "how much of the site is mined, a share",
        ["Views/Runs/Sections/MiningWindowSectionView.axaml|SiteRemainingLabel"] = "how much of the site is mined, a share",
        ["Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationEmptyText"] = "a fixed sentence saying no escalation was registered",
        ["Views/Runs/RunsSummaryPane.axaml|TopSites"] = "an item source; what its rows show is bound, and listed here, as Site",
    };

    /// <summary>Places not handled yet, each with what closes it. This list only shrinks.</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownGaps = _KnownGaps();

    private const int KnownGapCount = 76;

    [Fact]
    public void EveryLocationPlace_IsMaskedExemptOrAKnownGap()
    {
        List<string> undecided = _Places()
            .Where(place => !Masked.ContainsKey(place) && !Exempt.ContainsKey(place) && !KnownGaps.ContainsKey(place))
            .ToList();

        Assert.True(undecided.Count == 0,
            "Location shown without a decision. Mask it (OpsecText.Mark), or add it to Exempt with a reason:\n"
            + string.Join('\n', undecided));
    }

    [Fact]
    public void KnownGaps_OnlyShrink()
    {
        Assert.Equal(KnownGapCount, KnownGaps.Count);
    }

    [Fact]
    public void EveryListedPlace_StillExists()
    {
        HashSet<string> scanned = _Scan().ToHashSet();
        List<string> stale = Masked.Keys.Concat(Exempt.Keys).Concat(KnownGaps.Keys).Concat(Audited)
            .Distinct()
            .Where(place => !scanned.Contains(place) && !_SourceContains(place))
            .ToList();

        Assert.True(stale.Count == 0, "Listed but gone; remove the entry:\n" + string.Join('\n', stale));
    }

    [Fact]
    public void EveryMaskedPlace_NamesAnExistingTest()
    {
        HashSet<string> tests = typeof(OpsecCoverageTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToHashSet();

        List<string> unproven = Masked.Where(entry => !tests.Contains(entry.Value)).Select(entry => $"{entry.Key} → {entry.Value}").ToList();

        Assert.True(unproven.Count == 0, "Masked without a test proving it:\n" + string.Join('\n', unproven));
    }

    [Fact]
    public void Scan_RecognisesLocationBindings()
    {
        List<string> scanned = _Scan().ToList();

        Assert.Contains("Views/Runs/ActivityRowView.axaml|SiteText", scanned);
        Assert.Contains("Views/Home/HomePilotsBlock.axaml|SystemName", scanned);
        Assert.DoesNotContain(scanned, place => place.EndsWith("Command", StringComparison.Ordinal));
    }

    private static IEnumerable<string> _Places() => _Scan().Concat(Audited).Distinct();

    private static IEnumerable<string> _Scan()
    {
        string client = _ClientSourceRoot();
        return Directory.EnumerateFiles(client, "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(file => BindingPath.Matches(File.ReadAllText(file))
                .Select(match => match.Groups[1].Value.Split('.')[^1])
                .Where(property => LocationName.IsMatch(property) && !NotText.IsMatch(property))
                .Select(property => $"{Path.GetRelativePath(client, file).Replace('\\', '/')}|{property}"))
            .Distinct();
    }

    private static bool _SourceContains(string place)
    {
        string[] parts = place.Split('|', 2);
        string file = Path.Combine(_ClientSourceRoot(), parts[0]);
        return File.Exists(file) && File.ReadAllText(file).Contains(parts[1], StringComparison.Ordinal);
    }

    private static string _ClientSourceRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
                return Path.Combine(directory.FullName, "EveUtils.Client");
        }

        throw new InvalidOperationException("EVE-Together.slnx not found above the test output directory");
    }

    private static Dictionary<string, string> _KnownGaps()
    {
        string[] screens =
        [
            "Views/ActivityDetailWindow.axaml|SiteText",
            "Views/DpsOverlayWindow.axaml|LocationDisplay",
            "Views/EscalationDialogWindow.axaml|DestinationSecurityText",
            "Views/EscalationDialogWindow.axaml|DestinationSystem",
            "Views/EscalationDialogWindow.axaml|SiteResults",
            "Views/FleetMetricsWindow.axaml|LocationDisplay",
            "Views/Home/HomePilotsBlock.axaml|SystemDetailText",
            "Views/Home/HomePilotsBlock.axaml|SystemName",
            "Views/KillmailDetailWindow.axaml|SystemLineText",
            "Views/Killmails/KillmailLinkedRunView.axaml|SiteText",
            "Views/Killmails/KillmailRowView.axaml|SecurityText",
            "Views/Killmails/KillmailRowView.axaml|SystemLineText",
            "Views/ManualRunStartWindow.axaml|LocationName",
            "Views/ManualRunStartWindow.axaml|SignatureGroup",
            "Views/ManualRunStartWindow.axaml|SiteName",
            "Views/ManualRunStartWindow.axaml|SiteResults",
            "Views/MetricsWindow.axaml|LocationDisplay",
            "Views/Runs/ActivityRowView.axaml|SiteText",
            "Views/Runs/ActivityRowView.axaml|SystemLineText",
            "Views/Runs/ActivityRowView.axaml|SystemTooltip",
            "Views/Runs/RunningBand.axaml|SiteText",
            "Views/Runs/RunsActivityPane.axaml|SiteText",
            "Views/Runs/RunsSummaryPane.axaml|Site",
            "Views/Runs/Sections/ActivityDetailSectionView.axaml|LocationText",
            "Views/Runs/Sections/ActivityDetailSectionView.axaml|SignatureText",
            "Views/Runs/Sections/ActivityDetailSectionView.axaml|SiteText",
            "Views/Runs/Sections/ActivityWindowSectionView.axaml|EscalationRegisteredText",
            "Views/Runs/Sections/ActivityWindowSectionView.axaml|LocationText",
            "Views/Runs/Sections/ActivityWindowSectionView.axaml|SignatureSiteText",
            "Views/Runs/Sections/ActivityWindowSectionView.axaml|SignatureTypeText",
            "Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationExpiresAtText",
            "Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationJumpsEmptyText",
            "Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationJumpsText",
            "Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationObservedText",
            "Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationSystemText",
            "Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationText",
            "Views/Runs/Sections/MissionDetailSectionView.axaml|MissionLocationText",
            "Views/Runs/Sections/MissionWindowSectionView.axaml|MissionLocationText",
            .. Audited.Where(place => !place.StartsWith("ViewModels/Map/", StringComparison.Ordinal)),
        ];

        string[] map =
        [
            "Views/FleetMapCard.axaml|FleetSystemChips",
            "Views/FleetMapCard.axaml|SystemInfo",
            "Views/MapWindow.axaml|RegionName",
            "Views/MapWindow.axaml|RouteEndsText",
            "Views/MapWindow.axaml|RouteIndexes",
            "Views/MapWindow.axaml|RouteJumpsText",
            "Views/MapWindow.axaml|RouteMessage",
            "Views/MapWindow.axaml|RouteSteps",
            "Views/MapWindow.axaml|SecurityText",
            "Views/MapWindow.axaml|SelectedConstellationText",
            "Views/MapWindow.axaml|SelectedRegionText",
            "Views/MapWindow.axaml|SelectedSecurityText",
            "Views/MapWindow.axaml|SystemInfo",
            "Views/MapWindow.axaml|SystemName",
            "Views/MapWindow.axaml|SystemNames",
            "Views/MapWindow.axaml|TrailJumps",
            "ViewModels/Map/MapFleetSystemChip.cs|SystemName",
        ];

        return screens.Select(place => (place, PlannedScreens))
            .Concat(map.Select(place => (place, PlannedMap)))
            .ToDictionary(entry => entry.place, entry => entry.Item2);
    }
}
