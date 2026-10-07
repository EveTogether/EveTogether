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
    private const string MarkConverter = "Converter={x:Static opsec:OpsecMarkConverter.Instance}";
    private const string ConverterProof = nameof(OpsecCoverageTests) + "." + nameof(ConverterMarkedBindings_CarryTheConverter);
    private const string MapWindowProof = nameof(OpsecMapTests) + "." + nameof(OpsecMapTests.MapWindow_OpsecOn_DrawsNoMap_AndSaysWhy);
    private const string MapCardProof = nameof(OpsecMapTests) + "." + nameof(OpsecMapTests.FleetMapCard_OpsecOn_DrawsNoMap_AndSaysWhy);

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
        "ViewModels/Killmails/KillmailDetailViewModel.cs|linkedRun.SiteName",
        "ViewModels/Home/HomePilotRowViewModel.cs|SystemDetailText",
        "ViewModels/FleetMemberMenu.cs|_LocationLine",
        "Fleet/FleetCommanderPresence.cs|Tooltip",
        "Fleet/FleetRunsInProgress.cs|Describe",
        "ViewModels/GameLogs/GameLogRowViewModel.cs|Text",
        "ViewModels/Runs/RunPublisher.cs|Publish '",
        "Runs/FleetRunWindowPresenter.cs|_Where",
        "Runs/HomefrontDetector.cs|Could not offer a homefront run on {Site}",
        "Runs/HomefrontDetector.cs|Start Homefront run:",
        "Runs/StartupResumeNoticeService.cs|Stage",
        "Clipboard/ClipboardSignatureOffer.cs|DescribeSignature",
        "ViewModels/Activity/ActivityWindowViewModel.cs|Copied signature {Signature}",
        "ViewModels/Map/MapFleetSystemChip.cs|SystemName",
        "ViewModels/Activity/ActivityWindowViewModel.Compact.cs|CompactWhereText",
        "ViewModels/Activity/ActivityWindowViewModel.Compact.cs|CompactAlertWhereText",
    ];

    /// <summary>Places proven masked, each with the test (Class.Method) that proves it.</summary>
    private static readonly IReadOnlyDictionary<string, string> Masked = new Dictionary<string, string>
    {
        // Runs
        ["Views/Runs/ActivityRowView.axaml|SiteText"] = "RunsOverviewTests.RunSavedWhileScreenIsOpen_AppearsWithoutReopening",
        ["Views/Runs/ActivityRowView.axaml|SystemLineText"] = "OpsecSourceMarkingTests.ActivityRow_MarksSystemSecurityAndUnknownSystemId",
        ["Views/Runs/ActivityRowView.axaml|SystemTooltip"] = "OpsecSourceMarkingTests.ActivityRow_MarksSystemSecurityAndUnknownSystemId",
        ["Views/Runs/RunningBand.axaml|SiteText"] = "RunsOverviewTests.TwoCharactersRunningAtOnce_EachShowTheirOwnLane",
        // Both take the row's own SiteText, proven on the row.
        ["Views/Runs/RunsActivityPane.axaml|SiteText"] = "RunsOverviewTests.RunSavedWhileScreenIsOpen_AppearsWithoutReopening",
        ["Views/Runs/RunsSummaryPane.axaml|Site"] = "RunsOverviewTests.RunSavedWhileScreenIsOpen_AppearsWithoutReopening",
        ["ViewModels/Runs/RunsActivityPaneViewModel.cs|MetaText"] = "OpsecSourceMarkingTests.RunsActivityPane_MetaText_MarksSystemAndSignature",
        ["ViewModels/Runs/RunningGroupViewModel.cs|KindText"] = "OpsecSourceMarkingTests.RunningGroup_KindText_MarksTheSystem",
        ["ViewModels/Home/HomeDashboardViewModel.cs|IdleText"] = "OpsecSourceMarkingTests.HomeDashboard_IdleText_MarksTheLastSite",
        ["ViewModels/Runs/UnfinishedRunViewModel.cs|TitleText"] = "RunsOverviewTests.UndoingADiscard_PutsTheRunBackInUnfinishedWithoutReopening",
        ["Views/ActivityDetailWindow.axaml|SiteText"] = "ActivityDetailTests.UndoDelete_RestoresTheActivity",
        ["Views/Runs/Sections/ActivityDetailSectionView.axaml|LocationText"] = "ActivityDetailTests.LocationText_NamesTheSolarSystemFromTheSde_InBothLocationAndTheActivityHeader",
        ["Views/Runs/Sections/ActivityDetailSectionView.axaml|SiteText"] = "OpsecSourceMarkingTests.ActivityDetailSection_MarksSiteAndSignature",
        ["Views/Runs/Sections/ActivityDetailSectionView.axaml|SignatureText"] = "OpsecSourceMarkingTests.ActivityDetailSection_MarksSiteAndSignature",
        ["ViewModels/Runs/Sections/ActivityDetailSectionViewModel.cs|HeaderSummary"] = "ActivityDetailTests.LocationText_NamesTheSolarSystemFromTheSde_InBothLocationAndTheActivityHeader",
        ["Views/Runs/Sections/ActivityWindowSectionView.axaml|SignatureSiteText"] = "ActivityWindowTests.ASiteThatNamesItsHulls_PutsThemInTheirOwnRow",
        ["Views/Runs/Sections/ActivityWindowSectionView.axaml|LocationText"] = "ActivityWindowWiringTests.AJumpLineInTheGamelog_ReachesTheLocationRow",
        ["Views/Runs/Sections/ActivityWindowSectionView.axaml|EscalationRegisteredText"] = "OpsecSourceMarkingTests.EscalationRegistered_MarksSiteAndDestination",
        ["ViewModels/Runs/Sections/ActivityWindowSectionViewModel.cs|HeaderSummary"] = "ClipboardSignatureOfferTests.TheMeasuredLine_FillsTheActivitySectionFromTheCatalogue",
        ["ViewModels/Activity/ActivityWindowViewModel.cs|ClockHint"] = "OpsecSourceMarkingTests.ActivityWindow_ClockHint_MarksTheWaitingAndTheRunningSite",
        ["ViewModels/Activity/ActivityWindowViewModel.cs|_RowFor(sample.CharacterId).LocationText"] = "ActivityWindowCorrectionTests.TheFleetSection_NamesEveryMemberItHeardFrom_AndSaysWhatTheCountIsMadeOf",
        ["Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationText"] = "EscalationRegistrationTests.RegisteringAnEscalation_StoresSiteSystemAndDeadline_VisibleOnTheDetailScreen",
        ["Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationSystemText"] = "EscalationRegistrationTests.RegisteringAnEscalation_StoresSiteSystemAndDeadline_VisibleOnTheDetailScreen",
        ["Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationJumpsText"] = "EscalationJumpDistanceTests.JumpsNameTheirAnchor_AndStayVisibleAsAnEmptyStateWhenTheyCannotBeRead",
        ["Views/RunsWindow.axaml|EscalationSiteText"] = "OpsecSourceMarkingTests.OpenEscalationRow_MarksSiteSystemAndSourceSite",
        ["Views/RunsWindow.axaml|EscalationSystemText"] = "OpsecSourceMarkingTests.OpenEscalationRow_MarksSiteSystemAndSourceSite",
        ["Views/RunsWindow.axaml|SourceSiteText"] = "OpsecSourceMarkingTests.OpenEscalationRow_MarksSiteSystemAndSourceSite",
        ["Views/Runs/Sections/MissionDetailSectionView.axaml|MissionLocationText"] = "OpsecSourceMarkingTests.MissionSections_MarkTheMissionLocation",
        ["Views/Runs/Sections/MissionWindowSectionView.axaml|MissionLocationText"] = "OpsecSourceMarkingTests.MissionSections_MarkTheMissionLocation",
        ["ViewModels/Runs/LinkedLossViewModel.cs|LinkedLossRunChoice"] = "OpsecSourceMarkingTests.LinkedLossChoices_MarkTheSiteName",
        ["Views/EscalationDialogWindow.axaml|DestinationSecurityText"] = "EscalationDestinationSystemTests.TypedDestination_ResolvesIdAndSecurity_FromTheSdeAlone",
        ["Views/ManualRunStartWindow.axaml|SiteName"] = ConverterProof,
        ["Views/ManualRunStartWindow.axaml|SignatureGroup"] = ConverterProof,
        ["ViewModels/Runs/RunPublisher.cs|Publish '"] = "OpsecSourceMarkingTests.RunPublisher_ServerPrompt_CarriesTheMarkedSite",
        ["Runs/StartupResumeNoticeService.cs|Stage"] = "StartupResumeNoticeServiceTests.Staged_ShowsOneToastNamingTheRun_WithResumeAndKeepStopped",
        ["Runs/HomefrontDetector.cs|Start Homefront run:"] = "HomefrontDetectorTests.AnOffertorySigilSeenTwice_InAFleet_OffersTheRaidOnce_AndStartOpensItsRun",
        ["Runs/HomefrontDetector.cs|Could not offer a homefront run on {Site}"] = "OpsecSourceMarkingTests.HomefrontLogLine_MarksTheSite",
        ["Clipboard/ClipboardSignatureOffer.cs|DescribeSignature"] = "OpsecSourceMarkingTests.SignatureOffer_Toast_MarksSignatureIdAndName",
        ["ViewModels/Activity/ActivityWindowViewModel.cs|Copied signature {Signature}"] = "OpsecSourceMarkingTests.SignatureDecisionLog_MarksTheSiteName",
        ["ViewModels/Activity/ActivityWindowViewModel.Compact.cs|CompactWhereText"] = "CompactRunWindowTests.CompactWhereText_MarksSiteAndSystem_SoOpsecCanMaskThem",
        ["ViewModels/Activity/ActivityWindowViewModel.Compact.cs|CompactAlertWhereText"] = "CompactRunWindowTests.AnEscalationRegisteredOnTheRun_IsAChip_ThatUnfoldsSiteSystemAndDeadline_Masked",

        // Killmails
        ["Views/Killmails/KillmailRowView.axaml|SystemLineText"] = "OpsecScreenMarkingTests.KillmailRow_MarksSystemRegionAndSecurity",
        ["Views/Killmails/KillmailRowView.axaml|SecurityText"] = "OpsecScreenMarkingTests.KillmailRow_MarksSystemRegionAndSecurity",
        ["Views/KillmailDetailWindow.axaml|SystemLineText"] = "OpsecSourceMarkingTests.KillmailDetail_MarksSystemRegionSecurityAndTheLinkedSite",
        ["Views/KillmailDetailWindow.axaml|LocationSummaryText"] = "OpsecSourceMarkingTests.KillmailDetail_LocationSection_MarksTheNearestCelestialsAndTheStargateSecurity",
        ["Views/KillmailDetailWindow.axaml|SecurityText"] = "OpsecSourceMarkingTests.KillmailDetail_LocationSection_MarksTheNearestCelestialsAndTheStargateSecurity",
        ["Views/KillmailDetailWindow.axaml|Location"] = "OpsecSourceMarkingTests.KillmailDetailWindow_OpsecOn_ShowsNoSystemRegionOrCelestialName",
        ["Views/Killmails/KillmailLinkedRunView.axaml|SiteText"] = "OpsecSourceMarkingTests.KillmailDetail_MarksSystemRegionSecurityAndTheLinkedSite",
        ["ViewModels/Killmails/KillmailDetailViewModel.cs|linkedRun.SiteName"] = "OpsecSourceMarkingTests.KillmailDetail_MarksSystemRegionSecurityAndTheLinkedSite",

        // Fleet, metrics and overlays
        ["Views/DpsOverlayWindow.axaml|LocationDisplay"] = "OpsecScreenMarkingTests.DpsLocation_MarksTheSystem",
        ["Views/FleetMetricsWindow.axaml|LocationDisplay"] = "OfflineMemberLocationTests.AnOfflineCharacterOfOurs_ShowsNoSystem_AndAFleetMateIsUntouched",
        ["Views/MetricsWindow.axaml|LocationDisplay"] = "OfflineMemberLocationTests.TheMetricsWindow_FollowsAPilotLoggingBackIn",
        ["ViewModels/FleetMemberMenu.cs|_LocationLine"] = "FleetMemberMenuTests.MemberMenu_ShowsWhatTheCardDoesNot",
        ["Fleet/FleetCommanderPresence.cs|Tooltip"] = "OpsecScreenMarkingTests.CommanderPresence_Tooltip_MarksTheCommandersSystem",
        ["Fleet/FleetRunsInProgress.cs|Describe"] = "FleetStopTests.StopDialog_NamesTheRunsThatAreStillGoing",
        ["Runs/FleetRunWindowPresenter.cs|_Where"] = "FleetRunOfferToastTests.CommanderStart_OffersAToastNamingTheSiteAndSystem_AndOpensNothing",

        // Home and game logs
        ["Views/Home/HomePilotsBlock.axaml|SystemName"] = "HomePilotLocationTests.ALocationSetAfterTheHomeOpened_ShowsInThePilotRow",
        ["Views/Home/HomePilotsBlock.axaml|SystemDetailText"] = "OpsecSourceMarkingTests.HomePilotRow_SystemDetailText_MarksTheSecurity",
        ["ViewModels/Home/HomePilotRowViewModel.cs|SystemDetailText"] = "OpsecSourceMarkingTests.HomePilotRow_SystemDetailText_MarksTheSecurity",
        ["ViewModels/GameLogs/GameLogRowViewModel.cs|Text"] = "OpsecScreenMarkingTests.GameLogRow_TravelAndNotifyLines_AreMarkedWhole_FightsMiningAndBountiesAreNot",

        // The map is not drawn at all while OPSEC is on, so everything on it is hidden with it.
        ["Views/MapWindow.axaml|RegionName"] = MapWindowProof,
        ["Views/MapWindow.axaml|RouteEndsText"] = MapWindowProof,
        ["Views/MapWindow.axaml|RouteIndexes"] = MapWindowProof,
        ["Views/MapWindow.axaml|RouteJumpsText"] = MapWindowProof,
        ["Views/MapWindow.axaml|RouteMessage"] = MapWindowProof,
        ["Views/MapWindow.axaml|RouteSteps"] = MapWindowProof,
        ["Views/MapWindow.axaml|SecurityText"] = MapWindowProof,
        ["Views/MapWindow.axaml|SelectedConstellationText"] = MapWindowProof,
        ["Views/MapWindow.axaml|SelectedRegionText"] = MapWindowProof,
        ["Views/MapWindow.axaml|SelectedSecurityText"] = MapWindowProof,
        ["Views/MapWindow.axaml|SystemInfo"] = MapWindowProof,
        ["Views/MapWindow.axaml|SystemName"] = MapWindowProof,
        ["Views/MapWindow.axaml|SystemNames"] = MapWindowProof,
        ["Views/MapWindow.axaml|TrailJumps"] = MapWindowProof,
        ["Views/FleetMapCard.axaml|FleetSystemChips"] = MapCardProof,
        ["Views/FleetMapCard.axaml|SystemInfo"] = MapCardProof,
        ["ViewModels/Map/MapFleetSystemChip.cs|SystemName"] = MapCardProof,
    };

    /// <summary>Scan hits that are no location at all, each with the reason. A claim a reviewer has to agree with.</summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>
    {
        ["Views/FitDetailWindow.axaml|Signature"] = "a ship's signature radius, a fitting stat, not a scanned signature",
        ["Views/RunsWindow.axaml|OpenEscalations"] = "the list of open-escalation rows; each row's own site, system and source text is masked",
        ["Views/Widgets/WidgetManagerWindow.axaml|LocationNote"] = "fixed help text about the \"Include my location\" setting, never a system name",
        ["Views/FleetRosterWindow.axaml|EsiAutoApplyStructure"] = "whether the in-game wing and squad structure is applied, a fleet setting",
        ["Views/EscalationDialogWindow.axaml|SiteQuery"] = "the text the pilot is typing into a search box: masking an input makes it unusable",
        ["Views/ManualRunStartWindow.axaml|SiteQuery"] = "the text the pilot is typing into a search box: masking an input makes it unusable",
        ["Views/EscalationDialogWindow.axaml|DestinationSystem"] = "the text the pilot is typing into an input: masking an input makes it unusable",
        ["Views/ManualRunStartWindow.axaml|LocationName"] = "the text the pilot is typing into an input: masking an input makes it unusable",
        ["Views/EscalationDialogWindow.axaml|SiteResults"] = "the SDE's catalogue of every site type matching the typed text, the same for every pilot anywhere",
        ["Views/ManualRunStartWindow.axaml|SiteResults"] = "the SDE's catalogue of every site type matching the typed text, the same for every pilot anywhere",
        ["Views/ManualRunStartWindow.axaml|AsksSiteGroup"] = "a flag that switches part of the form on, no text",
        ["Views/ManualRunStartWindow.axaml|NeedsSite"] = "a flag that switches part of the form on, no text",
        ["Views/ManualRunStartWindow.axaml|SiteGroups"] = "the fixed list of site categories, the same for every pilot anywhere",
        ["Views/ManualRunStartWindow.axaml|SelectedSiteGroup"] = "one of the fixed site categories, the same for every pilot anywhere",
        ["Views/Home/HomeDashboardView.axaml|SystemStrip"] = "the app and server status strip: \"system\" as in software, not a solar system",
        ["Views/Runs/Sections/MiningWindowSectionView.axaml|SiteRemainingFraction"] = "how much of the site is mined, a share",
        ["Views/Runs/Sections/MiningWindowSectionView.axaml|SiteRemainingLabel"] = "how much of the site is mined, a share",
        ["Views/Runs/Sections/ActivityWindowSectionView.axaml|SignatureTypeText"] = "the kind of activity (combat site, mission, abyssal), which says what was done, never where",
        ["Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationEmptyText"] = "a fixed sentence saying no escalation was registered",
        ["Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationExpiresAtText"] = "when the escalation expires, a time",
        ["Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationObservedText"] = "when the escalation was read from the Agency, a time",
        ["Views/Runs/Sections/EscalationDetailSectionView.axaml|EscalationJumpsEmptyText"] = "why the jump count could not be read, never a count or a place",
        ["Views/KillmailDetailWindow.axaml|LocationHintText"] = "fixed sentences saying why no map is shown (abyssal, no position yet, no SDE data), never a place",
        ["Views/Runs/RunsSummaryPane.axaml|TopSites"] = "an item source; what its rows show is bound, and listed here, as Site",
    };

    /// <summary>Places not handled yet, each with what closes it. Empty since ET-417 masked them all; a new place never goes on it.</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownGaps = new Dictionary<string, string>();

    /// <summary>Bindings straight to a shared DTO, marked in the view through <c>OpsecMarkConverter</c>.</summary>
    private static readonly string[] ConverterMarked =
    [
        "Views/ManualRunStartWindow.axaml|SiteName",
        "Views/ManualRunStartWindow.axaml|SignatureGroup",
        "Views/ManualRunStartWindow.axaml|SelectedSite.Name",
        "Views/ManualRunStartWindow.axaml|SelectedSuggestion.SiteName",
        "Views/EscalationDialogWindow.axaml|SelectedSite.Name",
    ];

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
    public void KnownGaps_StayEmpty()
    {
        Assert.Empty(KnownGaps);
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
    public void ConverterMarkedBindings_CarryTheConverter()
    {
        List<string> unmarked = ConverterMarked
            .Where(place =>
            {
                string[] parts = place.Split('|', 2);
                string view = File.ReadAllText(Path.Combine(_ClientSourceRoot(), parts[0]));
                return !Regex.IsMatch(view, $@"\{{Binding {Regex.Escape(parts[1])}, {Regex.Escape(MarkConverter)}");
            })
            .ToList();

        Assert.True(unmarked.Count == 0, "Bound without OpsecMarkConverter:\n" + string.Join('\n', unmarked));
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
}
