using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using Microsoft.Extensions.DependencyInjection;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="ClipboardSignatureOffer"/> (ET-79): what the toast says about a copied scan signature, built only
/// from what the SDE actually carries — and (ET-158) the one copy that says it with a run instead of a card.
/// </summary>
public sealed class ClipboardSignatureOfferTests
{
    /// <summary>Raymond's own clipboard, byte for byte: real tabs, and the comma decimals his client writes. Kept
    /// unwashed on purpose — a tidied fixture would stop proving the parser survives the thing it actually meets.</summary>
    private const string MeasuredHomefrontLine =
        "IMM-760	Cosmic Anomaly	Homefront Operation Site - Combat Site	Suspicious Signal: Secure the Intel	100,0%	0,50 AU";

    // ET-178 AC-3: a catalogue miss states the name and nothing else — not "not in the site catalogue" (a fact about
    // what is missing) and not an English-only fallback either (ET-79 AC-4).
    [AvaloniaFact]
    public async Task UnmatchedSignatureName_JustNamesTheSignature_SayingNeitherWhatIsMissingNorAnEnglishLimitation()
    {
        using var env = await Env.StartAsync();

        // Two recognised rows, because one on its own no longer produces a card at all (ET-158) and this is about
        // what the card says.
        env.Copy("KDC-304\tCosmic Signature\tCombat Site\tRuined Blood Raider Crystal Quarry\t100.0%\t2.71 AU\r\n" +
                 "KDC-305\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t1.10 AU");

        var message = Assert.Single(env.Toasts.ActionToasts).Message;
        Assert.Contains("KDC-304 · Ruined Blood Raider Crystal Quarry", message);
        Assert.DoesNotContain("not in the site catalogue", message);
        Assert.DoesNotContain("English", message);
    }

    [AvaloniaFact]
    public async Task MultipleDistinctMatches_SharesOnlyWhatTheyAgreeOn_AndNeverFabricatesOrPicksOne()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddSite(Site(1263, "Haunted Yard", archetype: "Combat Sites")); // no DED rating carried at all
        env.Sde.AddSite(Site(2001, "Guardian's Gala", archetype: "Combat Sites", faction: "Blood Raiders"));
        env.Sde.AddSite(Site(2002, "Guardian's Gala", archetype: "Combat Sites", faction: "Guristas"));

        env.Copy("AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU\r\n" +
                 "BBB-002\tCosmic Signature\tCombat Site\tGuardian's Gala\t100.0%\t1.10 AU");

        var message = Assert.Single(env.Toasts.ActionToasts).Message;
        Assert.NotNull(message);
        // Single match with no DED in the SDE: never rendered as "DED 0" (the DedRating ?? 0 mistake).
        Assert.Contains("Haunted Yard — Combat Sites", message);
        Assert.DoesNotContain("DED", message);
        // Two matches that disagree on faction: only the shared archetype plus a count, neither faction alone.
        Assert.Contains("Guardian's Gala — Combat Sites · 2 variants", message);
        Assert.DoesNotContain("Blood Raiders", message);
        Assert.DoesNotContain("Guristas", message);
    }

    [AvaloniaFact]
    public async Task CopyWithNamedAndUnnamedSignatures_ShowsOneToast_NamedLinesPlusOneSummaryLineForTheRest()
    {
        using var env = await Env.StartAsync();

        env.Copy("AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU\r\n" +
                 "BBB-002\tCosmic Signature\tCombat Site\tRuined Blood Raider Crystal Quarry\t100.0%\t1.10 AU\r\n" +
                 "CCC-003\tCosmic Signature\t\t\t25.0%\t-\r\n" +
                 "DDD-004\tCosmic Signature\t\t\t10.0%\t-\r\n" +
                 "EEE-005\tCosmic Signature\t\t\t5.0%\t-");

        var message = Assert.Single(env.Toasts.ActionToasts).Message;
        Assert.NotNull(message);
        var lines = message.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("3 more not fully scanned yet", lines[^1]);
    }

    [AvaloniaFact]
    public async Task RecopyingTheSamePayload_WhileTheCardIsOpen_DoesNotStackACard_ButAsksAgainAfterItCloses()
    {
        using var env = await Env.StartAsync();
        // Two recognised rows: one on its own goes straight to a run and never puts a card up (ET-158).
        const string text = "AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU\r\n" +
                            "BBB-002\tCosmic Signature\tCombat Site\tGuardian's Gala\t100.0%\t1.10 AU";

        env.Copy(text);
        env.Copy(text); // same payload, card still open — must not stack a second card
        Assert.Single(env.Toasts.ActionToasts);

        env.Toasts.ActionToasts[0].Actions[0].Run(); // "Close"
        env.Copy(text);
        Assert.Equal(2, env.Toasts.ActionToasts.Count);
    }

    // ── ET-158 — one fully-scanned combat site starts its own run ───────────────────────────────────

    // ET-158: the one case this feature can act on no longer offers a button, it starts the run. No card at all —
    // the pilot is in EVE, where an in-window toast is not visible anyway, and the run coming up is the answer.
    // The three rows are the three shapes the scan window writes that column in, plus one unscanned line to prove
    // it is the recognised row that counts and not the row count.
    [AvaloniaTheory]
    [InlineData("AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU\r\n" +
                "CCC-003\tCosmic Signature\t\t\t25.0%\t-")]
    [InlineData("AAA-001\tCosmic Anomaly\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU")]
    [InlineData(MeasuredHomefrontLine)]
    public async Task AFullyScannedCombatSite_StartsItsRunItself_WithNoCardAtAll(string copied)
    {
        using var env = await Env.StartAsync();
        // Registered in the catalogue on purpose — same as a real scan, where the site usually is one it knows —
        // to prove starting still works when it does (ET-178 leaves that path alone; a miss is covered elsewhere).
        env.Sde.AddSite(Site(1263, "Haunted Yard", archetype: "Combat Sites", archetypeId: 24));
        env.Sde.AddSite(Site(1264, "Suspicious Signal: Secure the Intel", archetype: "Homefront Operations", archetypeId: 70));

        env.Copy(copied);

        Assert.Empty(env.Toasts.ActionToasts);
        Assert.Empty(env.Toasts.Toasts);
        Assert.True(Assert.Single(env.Dialogs.ShownActivityWindows).StartsOnArrival);
    }

    /// <summary>
    /// ET-158 AC-4, in the order Raymond asked for on 2026-09-04: the question comes BEFORE the window, not beside
    /// it. He has two clients up, and what he got was an empty SITE RUN window — "no character yet", "not started",
    /// "no fit: the run has no character yet" — with "Whose run is this?" appearing next to it a moment later. That
    /// is what asking at START costs: START only runs once the window is loaded.
    ///
    /// The ordering is the assertion, not a side effect of it: the pick records how many windows had been opened at
    /// the moment it was asked, and that has to be none. Assert only that both happened and this passes on exactly
    /// the behaviour being replaced.
    ///
    /// Three rows, because the rule is "settle the pilot without asking a question that is already answered":
    /// ask at two clients, do not ask at one, and do not ask again when the window that is up already has a pilot.
    /// The second and third rows are the halves that must NOT put a modal on screen — that dialog takes the keyboard
    /// off EVE, which is the one thing ET-158 exists to avoid.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true, false, 90000002, "Second Pilot")]
    [InlineData(false, false, 90000001, "First Pilot")]
    [InlineData(true, true, null, null)]
    public async Task ThePilotIsSettledBeforeTheWindowIsOpened(
        bool twoClientsUp, bool windowAlreadyHasPilot, int? expectedId, string? expectedName)
    {
        int[] flying = twoClientsUp ? [] : [90000001];   // no ids at all means every character is in game
        using var env = await Env.StartAsync(services =>
            services.AddSingleton<ILocalCharacterPresence>(
                new ActivityWindowHarness.StubPresence(inGame: true, flying)));
        var registry = env.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("First Pilot", 90000001));
        await registry.AddOrUpdateAsync(new Character("Second Pilot", 90000002));
        // Registered in the catalogue so the window opens with its archetype filled in; not required to auto-start
        // (ET-178), just kept realistic for a row this test also inspects.
        env.Sde.AddSite(Site(1264, "Suspicious Signal: Secure the Intel", archetype: "Homefront Operations", archetypeId: 70));
        if (windowAlreadyHasPilot)
            env.Dialogs.ActivityWindowPilot = (90000001, "First Pilot");

        var windowsOpenWhenAsked = -1;
        env.Dialogs.OnPickCharacter = (_, _) =>
        {
            windowsOpenWhenAsked = env.Dialogs.ShownActivityWindows.Count;
            return Task.FromResult<int?>(90000002);
        };

        env.Copy(MeasuredHomefrontLine);
        await ActivityWindowHarness.WaitUntil(() => env.Dialogs.ShownActivityWindows.Count > 0);

        var shouldAsk = twoClientsUp && !windowAlreadyHasPilot;
        Assert.Equal(shouldAsk ? "Whose run is this?" : null, env.Dialogs.LastPrompt);
        Assert.Equal(shouldAsk ? 0 : -1, windowsOpenWhenAsked);   // asked, and asked with no window open yet
        ActivityWindowViewModel opened = Assert.Single(env.Dialogs.ShownActivityWindows);
        // Row three carries no pilot on purpose: the window already up keeps its own, and DialogService leaves it be.
        Assert.Equal(expectedId is { } id ? (id, expectedName!) : null, opened.PickedCharacter);
        Assert.True(opened.StartsOnArrival);
    }

    // ── ET-177 — the catalogue decides, for every archetype it carries ──────────────────────────────

    // AC-1 + AC-3: Emergency Aid: Convoy is a Homefront Operations dungeon (measured build 3492266) — a name whose
    // group column never says "Combat Site". Without the fix this row is invisible to the old group-text check and
    // nothing happens; the empty and fabricated-foreign-language rows prove the column itself carries no vote once
    // the catalogue confirms the name (Raymond, 2026-09-04: the catalogue hit is the whole bar — no per-archetype
    // list to guess at or maintain).
    [AvaloniaTheory]
    [InlineData("XXX-001\tCosmic Anomaly\t\tEmergency Aid: Convoy\t100.0%\t1.00 AU")]
    [InlineData("XXX-001\tCosmic Anomaly\tOpération de front intérieur\tEmergency Aid: Convoy\t100.0%\t1.00 AU")]
    public async Task ACatalogueKnownHomefront_StartsItsRun_RegardlessOfTheGroupColumn(string copied)
    {
        using var env = await Env.StartAsync();
        env.Sde.AddSite(Site(1265, "Emergency Aid: Convoy", archetype: "Homefront Operations", archetypeId: 70));

        env.Copy(copied);

        Assert.Empty(env.Toasts.ActionToasts);
        Assert.True(Assert.Single(env.Dialogs.ShownActivityWindows).StartsOnArrival);
    }

    // AC-5: the existing "2+ rows is a menu" rule holds for the widened set too, not only for combat.
    [AvaloniaFact]
    public async Task TwoCatalogueKnownHomefronts_ShowNoStartRunButton()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddSite(Site(1265, "Emergency Aid: Convoy", archetype: "Homefront Operations", archetypeId: 70));
        env.Sde.AddSite(Site(1267, "Raid: Narcotics Lab", archetype: "Homefront Operations", archetypeId: 70));

        env.Copy("XXX-001\tCosmic Anomaly\t\tEmergency Aid: Convoy\t100.0%\t1.00 AU\r\n" +
                 "XXX-003\tCosmic Anomaly\t\tRaid: Narcotics Lab\t100.0%\t1.00 AU");

        Assert.Empty(env.Dialogs.ShownActivityWindows);
        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Equal(new[] { "Close" }, Array.ConvertAll(offer.Actions.ToArray(), a => a.Label));
    }

    // ── ET-178 — the catalogue enriches; it never gates whether a scan becomes a run ────────────────

    // AC-1: a fully-scanned row starts its run on the name the clipboard gave, whether or not the SDE has ever
    // heard of it. Data Site, Relic Site and Wormhole are never in the catalogue at all (that is the expected
    // case, not a fault); gas sites and sleeper caches share exactly this property and need no ticket of their
    // own. Without the fix this is a "not in the site catalogue" toast and no run — the tegenproef.
    [AvaloniaTheory]
    [InlineData(MeasuredHomefrontLine)]
    [InlineData("AAA-001	Anomalie cosmique	Site de combat	Haunted Yard	100,0%	0,50 AU")]
    [InlineData("QLY-810\tCosmic Signature\tWormhole\tUnstable Wormhole\t100.0%\t11.66 AU")]
    public async Task ASiteTheCatalogueDoesNotCarryAtAll_StillStartsItsRunOnTheCopiedName(string copied)
    {
        using var env = await Env.StartAsync(); // catalogue deliberately empty

        env.Copy(copied);

        Assert.Empty(env.Toasts.ActionToasts);
        Assert.True(Assert.Single(env.Dialogs.ShownActivityWindows).StartsOnArrival);
    }

    // AC-5: a name the catalogue does carry, under a different signature, is still a miss for THIS name — no
    // fuzzy fallback borrows another site's row. The fixture never models locale aliasing (see FakeSdeAccessor),
    // so a French name it never registered is exactly this case.
    [AvaloniaFact]
    public async Task ASiteNameTheCatalogueDoesNotCarry_StillStartsItsRun_WithoutBorrowingAnotherSitesData()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddSite(Site(1263, "Suspicious Signal: Secure the Intel", ded: 4));

        env.Copy("IMM-760	Cosmic Anomaly	Homefront Operation Site - Combat Site	Signal suspect : sécuriser les renseignements	100,0%	0,50 AU");

        Assert.Empty(env.Toasts.ActionToasts);
        var opened = Assert.Single(env.Dialogs.ShownActivityWindows);
        Assert.True(opened.StartsOnArrival);
        Assert.Empty(opened.MatchedSites);
    }

    // AC-1 tegenproef: the same single row, but under the scan threshold — Group/Name null is the normal
    // not-yet-scanned state, and a button on it would open an empty window.
    [AvaloniaFact]
    public async Task TheOneRowUnderTheScanThreshold_ShowsNoStartRunButton()
    {
        using var env = await Env.StartAsync();

        env.Copy("CCC-003\tCosmic Signature\t\t\t25.0%\t-");

        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Equal(new[] { "Close" }, Array.ConvertAll(offer.Actions.ToArray(), a => a.Label));
    }

    // AC-2: two or more recognised rows is a menu, and a toast is the wrong surface for one.
    [AvaloniaFact]
    public async Task TwoRecognisedRows_ShowsNoStartRunButton()
    {
        using var env = await Env.StartAsync();

        env.Copy("AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU\r\n" +
                 "BBB-002\tCosmic Signature\tCombat Site\tGuardian's Gala\t100.0%\t1.10 AU");

        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Equal(new[] { "Close" }, Array.ConvertAll(offer.Actions.ToArray(), a => a.Label));
    }

    // AC-6: a copy with no recognised rows at all keeps behaving exactly as ET-79 left it.
    [AvaloniaFact]
    public async Task CopyWithNoRecognisedRows_IsUnchangedFromET79()
    {
        using var env = await Env.StartAsync();

        env.Copy("CCC-003\tCosmic Signature\t\t\t25.0%\t-\r\nDDD-004\tCosmic Signature\t\t\t10.0%\t-");

        var offer = Assert.Single(env.Toasts.ActionToasts);
        Assert.Equal(new[] { "Close" }, Array.ConvertAll(offer.Actions.ToArray(), a => a.Label));
        Assert.Equal("2 more not fully scanned yet", offer.Message);
    }

    // AC-3: the window opens with what the clipboard actually said, not a placeholder. And it must not reach for the
    // keyboard: the whole point of ET-158 is that the pilot never leaves EVE, so a window that grabs focus mid-fight
    // costs exactly what ET-105 AC-2 said it costs.
    [AvaloniaFact]
    public async Task ACopiedCombatSite_OpensTheActivityWindow_WithTheRowsGroupAndName_AndWithoutTakingFocus()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddSite(Site(1263, "Haunted Yard", archetype: "Combat Sites", archetypeId: 24));

        env.Copy("AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU");

        var opened = Assert.Single(env.Dialogs.ShownActivityWindows);
        Assert.Equal(ActivityKind.Site, opened.Kind);
        Assert.Equal("Combat Site", opened.SignatureGroup);
        Assert.Equal("Haunted Yard", opened.SignatureName);
        Assert.Equal(RunWindowOpenTrigger.CopiedFromClipboard,
            Assert.Single(env.Dialogs.ShownActivityWindowTriggers));
    }

    // Tegenproef: the clipboard can report one copy more than once, and there is no button left whose own guard
    // would catch it. Without the fingerprint guard this is two windows and two runs.
    [AvaloniaFact]
    public async Task TheSameCopyReportedTwice_OpensOnlyOneWindow()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddSite(Site(1263, "Haunted Yard", archetype: "Combat Sites", archetypeId: 24));
        const string text = "AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU";

        env.Copy(text);
        env.Copy(text);

        Assert.Single(env.Dialogs.ShownActivityWindows);
    }

    // ── The ACTIVITY section, filled from the catalogue ─────────────────────────────────────────────

    [AvaloniaFact]
    public async Task TheMeasuredLine_FillsTheActivitySectionFromTheCatalogue()
    {
        using var env = await Env.StartAsync();
        env.Sde.AddSite(Site(1263, "Suspicious Signal: Secure the Intel", archetype: "Homefront Operations",
            archetypeId: 70, faction: "Caldari State", ded: 4, restricted: true,
            groups: [new SdeGroup(420, 6, "Destroyer", true), new SdeGroup(25, 6, "Frigate", true)]));

        env.Copy(MeasuredHomefrontLine);

        var opened = Assert.Single(env.Dialogs.ShownActivityWindows);
        // TYPE stays the scan window's own words: the SDE carries no scanner-type mapping to enrich it with.
        Assert.Equal("Homefront Operation Site - Combat Site", opened.SignatureTypeText);
        // One description, and the toast that opened this window used the same one.
        Assert.Equal("Suspicious Signal: Secure the Intel — Homefront Operations · Caldari State · DED 4 · ship-restricted",
            opened.SignatureSiteText);
        Assert.Equal("Destroyer, Frigate", opened.ShipRestrictionText);
        Assert.Equal("Suspicious Signal: Secure the Intel · Homefront Operations · Caldari State · DED 4 · ship-restricted",
            opened.Activity.HeaderSummary);
    }

    private static SdeSite Site(int dungeonId, string name, string? archetype = null, int? archetypeId = null,
        string? faction = null, int? ded = null, bool restricted = false, IReadOnlyList<SdeGroup>? groups = null) =>
        new(dungeonId, name, archetypeId ?? (archetype is null ? null : 1), archetype, faction is null ? null : 1,
            faction, null, ded, restricted, groups ?? []);

    private sealed class Env : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly ClipboardWatchService _watch;
        private readonly ClipboardSignatureOffer _offer;
        private readonly FakeClipboardChangeSource _source;

        public RecordingToastService Toasts { get; } = new();

        public FakeSdeAccessor Sde { get; } = new();

        public RecordingDialogService Dialogs { get; } = new();

        public IServiceProvider Services => _instance.Services;

        private Env(TestClientInstance instance, ClipboardWatchService watch, FakeClipboardChangeSource source)
        {
            _instance = instance;
            _watch = watch;
            _source = source;
            _offer = new ClipboardSignatureOffer(watch, Toasts, Sde, Dialogs, instance.Services);
        }

        public static async Task<Env> StartAsync(Action<IServiceCollection>? configure = null)
        {
            var source = new FakeClipboardChangeSource();
            var instance = TestClientInstance.Create(configure);
            var watch = new ClipboardWatchService(new RecordingDialogService(), instance.Services,
                NullLogger<ClipboardWatchService>.Instance, source);
            var env = new Env(instance, watch, source);
            await watch.SetEnabledAsync(true);
            return env;
        }

        public void Copy(string text)
        {
            _source.ClipboardText = text;
            _source.RaiseChanged();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            _offer.Dispose();
            _watch.Dispose();
            _instance.Dispose();
        }
    }

    private sealed class FakeClipboardChangeSource : IClipboardChangeSource
    {
        public string? ClipboardText { get; set; }

        public bool IsSupported => true;

        public event Action? Changed;

        public event Action? SupportChanged
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public Task<string?> ReadTextAsync() => Task.FromResult(ClipboardText);

        public void RaiseChanged() => Changed?.Invoke();
    }
}
