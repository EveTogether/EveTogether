using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-24: the ESI chip must say what THIS account's session is worth, and keep saying it.
///
/// <para>
/// The reported symptom is a contradiction on screen: the startup check names two expired characters in a
/// modal, and every chip underneath it is blue with a tick — including those two. The cause is that the chip
/// was derived from two row-level booleans ("a token file exists" + a re-auth flag set once at startup), and
/// the character list is rebuilt from scratch whenever the registry is written — which a successful token
/// refresh does. One character's refresh therefore reset every other character's row to the defaults.
/// </para>
/// <para>
/// So these tests check the rendered chip, not just a property: green tests said nothing about what the
/// operator saw five tickets in a row on this project.
/// </para>
/// </summary>
public class EsiPerAccountStatusTests
{
    // The six characters from the operator's screenshot, in the order they appear there.
    private const int Jithran = 96000001;
    private const int Abnoba = 96000002;
    private const int HotSprockets = 96000003;   // expired — named in the dialog
    private const int ColdSprockets = 96000004;  // token expiring, refreshes fine, and its grant changed
    private const int Noahmarr = 96000005;
    private const int LyraCustos = 96000006;     // expired — named in the dialog

    private static readonly (int Id, string Name)[] Roster =
    [
        (Jithran, "Jithran"), (Abnoba, "Abnoba Auscent"), (HotSprockets, "HotSprockets"),
        (ColdSprockets, "ColdSprockets"), (Noahmarr, "Noahmarr"), (LyraCustos, "Lyra Custos"),
    ];

    // ── The operator's startup, end to end ───────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Startup_LeavesExactlyTheExpiredAccountsWarning_EvenThoughAnotherCharacterRefreshed()
    {
        var (instance, dialogs, auth) = await StartupScenarioAsync();
        using (instance)
        {
            var vm = new MainWindowViewModel(instance.Services);
            await WaitForStartupCheckAsync(dialogs);

            // The dialog is the operator's evidence that the check itself is right.
            Assert.NotNull(dialogs.LastMessage);
            Assert.Contains("HotSprockets", dialogs.LastMessage);
            Assert.Contains("Lyra Custos", dialogs.LastMessage);

            // ColdSprockets' refresh really did happen, and really did write the new grant to the registry —
            // which is the rebuild that used to wipe the other two rows.
            Assert.True(auth.RefreshCalls >= 3);
            Assert.Equal(TokenStatus.Refreshed, Tracker(instance).Get(ColdSprockets));

            await SettleAsync();
            AssertWarning(vm, HotSprockets, LyraCustos);
        }
    }

    [AvaloniaFact]
    public async Task Startup_RendersAnAmberChipForExactlyTheExpiredAccounts()
    {
        var (instance, dialogs, _) = await StartupScenarioAsync();
        using (instance)
        {
            var vm = new MainWindowViewModel(instance.Services);
            await WaitForStartupCheckAsync(dialogs);
            await SettleAsync();

            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Iron Law #9: leave the frame behind so the chips can be looked at, not only asserted about.
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(Path.GetTempPath(), "eveutils-esi-chips-per-account.png"),
                new PngBitmapEncoderOptions());

            var chips = EsiChips(window);
            Assert.Equal(Roster.Length, chips.Count); // one ESI chip per character row, no more

            var warned = chips.Where(c => c.Border.Classes.Contains("warn")).Select(c => c.Row.CharacterId).ToList();
            Assert.Equal([HotSprockets, LyraCustos], warned.Order().ToList());

            foreach (var chip in chips)
            {
                var expired = chip.Row.CharacterId is HotSprockets or LyraCustos;
                Assert.Equal(expired ? "ESI ⚠" : "ESI ✓", chip.Text);
                Assert.Equal(expired, chip.Border.Classes.Contains("warn"));
                Assert.Equal(!expired, chip.Border.Classes.Contains("ok")); // and never both at once
            }

            window.Close();
        }
    }

    // ── The rebuild seam itself ──────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Rebuild_KeepsEachCharactersOwnStatus_InsteadOfResettingEveryRow()
    {
        using var instance = TestClientInstance.Create();
        await RegisterRosterAsync(instance);
        await StoreTokenAsync(instance, Roster.Select(r => r.Id).ToArray(), DateTimeOffset.UtcNow.AddHours(1));

        var vm = new MainWindowViewModel(instance.Services);
        await WaitForStartupCheckAsync(instance);

        var tracker = Tracker(instance);
        await tracker.RecordAsync(HotSprockets, TokenStatus.NeedsReauth);
        await tracker.RecordAsync(LyraCustos, TokenStatus.NeedsReauth);

        // The rebuild every sign-in and every registry write triggers.
        await vm.RefreshCharactersAsync();
        await SettleAsync();

        AssertWarning(vm, HotSprockets, LyraCustos);
    }

    [AvaloniaFact]
    public async Task RegistryWrite_ForOneCharacter_DoesNotClearAnothersWarning()
    {
        using var instance = TestClientInstance.Create();
        await RegisterRosterAsync(instance);
        await StoreTokenAsync(instance, Roster.Select(r => r.Id).ToArray(), DateTimeOffset.UtcNow.AddHours(1));

        var vm = new MainWindowViewModel(instance.Services);
        await WaitForStartupCheckAsync(instance);
        await Tracker(instance).RecordAsync(HotSprockets, TokenStatus.NeedsReauth);
        await SettleAsync();
        AssertWarning(vm, HotSprockets);

        // Exactly what a successful background refresh of a different character does (ClientTokenRefreshService).
        await instance.Services.GetRequiredService<ICharacterRegistry>()
            .AddOrUpdateAsync(new Character("ColdSprockets", ColdSprockets, ["publicData", "esi-skills.read_skills.v1"]));
        await SettleAsync();

        AssertWarning(vm, HotSprockets);
    }

    [AvaloniaFact]
    public async Task SignIn_ClearsAStaleWarning_ForThatCharacterOnly()
    {
        using var instance = TestClientInstance.Create();
        await RegisterRosterAsync(instance);
        await StoreTokenAsync(instance, Roster.Select(r => r.Id).ToArray(), DateTimeOffset.UtcNow.AddHours(1));

        var vm = new MainWindowViewModel(instance.Services);
        await WaitForStartupCheckAsync(instance);

        var tracker = Tracker(instance);
        await tracker.RecordAsync(HotSprockets, TokenStatus.NeedsReauth);
        await tracker.RecordAsync(LyraCustos, TokenStatus.NeedsReauth);
        await vm.RefreshCharactersAsync();
        await SettleAsync();
        AssertWarning(vm, HotSprockets, LyraCustos);

        // What LocalEsiLoginService records once a fresh grant is stored — for the one character that signed in.
        await tracker.RecordAsync(HotSprockets, TokenStatus.Valid);
        await vm.RefreshCharactersAsync();
        await SettleAsync();

        AssertWarning(vm, LyraCustos);
    }

    // ── TemporarilyUnavailable used to read as connected ─────────────────────────────────────────────

    [AvaloniaFact]
    public async Task TemporarilyUnavailable_DoesNotRenderAsConnected()
    {
        using var instance = TestClientInstance.Create();
        await RegisterRosterAsync(instance);
        await StoreTokenAsync(instance, Roster.Select(r => r.Id).ToArray(), DateTimeOffset.UtcNow.AddHours(1));

        var vm = new MainWindowViewModel(instance.Services);
        await WaitForStartupCheckAsync(instance);
        await Tracker(instance).RecordAsync(Noahmarr, TokenStatus.TemporarilyUnavailable);
        await vm.RefreshCharactersAsync();
        await SettleAsync();

        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var chip = Assert.Single(EsiChips(window), c => c.Row.CharacterId == Noahmarr);
        Assert.DoesNotContain("ok", chip.Border.Classes); // nothing works — it must not read as a healthy session
        Assert.Contains("warn", chip.Border.Classes);
        Assert.Equal("ESI ⏳", chip.Text);
        Assert.Contains("temporarily unavailable", chip.Row.EsiStatus);

        window.Close();
    }

    // ── The refresh service: what it records, what it writes, and how it serializes ──────────────────

    [Fact]
    public async Task Refresh_RecordsTheOutcomePerCharacter_OnTheTracker()
    {
        var ct = TestContext.Current.CancellationToken;
        var tracker = new EsiTokenStatusTracker(new InProcessEventBus());
        var service = Refresher(tracker,
            new MapTokenStore { [Jithran] = Fresh(), [HotSprockets] = Stale("revoked") },
            new ScriptedAuthClient(), new MapJwtValidator());

        Assert.Equal(TokenStatus.Valid, await service.EnsureValidAsync(Jithran, ct));
        Assert.Equal(TokenStatus.NeedsReauth, await service.EnsureValidAsync(HotSprockets, ct));

        Assert.Equal(TokenStatus.Valid, tracker.Get(Jithran));
        Assert.Equal(TokenStatus.NeedsReauth, tracker.Get(HotSprockets));
    }

    [Fact]
    public async Task Refresh_WritesToTheRegistry_OnlyWhenTheGrantActuallyChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = new CountingRegistry
        {
            Characters = [new Character("ColdSprockets", ColdSprockets, ["esi-skills.read_skills.v1", "publicData"])],
        };
        var auth = new ScriptedAuthClient { ["good"] = Fresh("refreshed") };
        var validator = new MapJwtValidator();
        var store = new MapTokenStore { [ColdSprockets] = Stale("good") };
        var service = Refresher(new EsiTokenStatusTracker(new InProcessEventBus()), store, auth, validator, registry);

        // Same grant, listed in a different order — EVE guarantees no ordering, so this is not a change and must
        // not write. A write raises RegistryChanged, which rebuilds the whole character list; doing that after
        // every refresh meant the 60 s loop could rebuild it every minute for nothing (ET-24).
        validator["refreshed"] = new EsiIdentity(ColdSprockets, "ColdSprockets", ["publicData", "esi-skills.read_skills.v1"]);
        Assert.Equal(TokenStatus.Refreshed, await service.EnsureValidAsync(ColdSprockets, ct));
        Assert.Equal(0, registry.Writes);

        // A scope really being added IS a change the rest of the app has to see. (The refresh above stored a fresh
        // token, so put an expiring one back to take the refresh path again rather than "still valid".)
        store[ColdSprockets] = Stale("good");
        validator["refreshed"] = new EsiIdentity(ColdSprockets, "ColdSprockets",
            ["publicData", "esi-skills.read_skills.v1", "esi-clones.read_implants.v1"]);
        Assert.Equal(TokenStatus.Refreshed, await service.EnsureValidAsync(ColdSprockets, ct));
        Assert.Equal(1, registry.Writes);
    }

    [Fact]
    public async Task Refresh_OfOneCharacter_IsSerialized_SoTwoCallersNeverRaceOnTheSameRefreshToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = new ScriptedAuthClient { ["good"] = Fresh("refreshed") } ;
        var store = new MapTokenStore { [ColdSprockets] = Stale("good") };
        var validator = new MapJwtValidator { ["refreshed"] = new EsiIdentity(ColdSprockets, "ColdSprockets", ["publicData"]) };
        var service = Refresher(new EsiTokenStatusTracker(new InProcessEventBus()), store, auth, validator);

        // The 60 s loop and an ESI call arriving together — the exact shape of the race.
        var both = await Task.WhenAll(service.EnsureValidAsync(ColdSprockets, ct), service.EnsureValidAsync(ColdSprockets, ct));

        Assert.All(both, s => Assert.True(s is TokenStatus.Refreshed or TokenStatus.Valid));
        Assert.Equal(1, auth.RefreshCalls); // the second caller waited and then found a fresh token, not a second SSO round-trip
    }

    [Fact]
    public async Task Tracker_PublishesPerCharacterEvents_AndStaysQuietOnARepeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new InProcessEventBus();
        var refreshed = new List<TokenStatusChange>();
        var failed = new List<TokenStatusChange>();
        using var a = bus.Subscribe<TokenRefreshedEvent>((e, _) => { refreshed.Add(e.Data); return Task.CompletedTask; });
        using var b = bus.Subscribe<TokenRefreshFailedEvent>((e, _) => { failed.Add(e.Data); return Task.CompletedTask; });

        var tracker = new EsiTokenStatusTracker(bus);
        await tracker.RecordAsync(Jithran, TokenStatus.Valid, ct);
        await tracker.RecordAsync(Jithran, TokenStatus.Valid, ct);   // unchanged → no second event
        await tracker.RecordAsync(HotSprockets, TokenStatus.NeedsReauth, ct);

        Assert.Equal([Jithran], refreshed.Select(c => c.CharacterId));
        Assert.Equal([new TokenStatusChange(HotSprockets, TokenStatus.NeedsReauth)], failed);
    }

    // ── Scenario plumbing ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The operator's machine on startup: six registered characters, four with a token that is still good,
    /// ColdSprockets with one that is expiring but refreshes (and comes back with a wider grant, so the registry
    /// is written and the character list is rebuilt), HotSprockets and Lyra Custos with a refresh token EVE SSO
    /// rejects.
    /// </summary>
    private static async Task<(TestClientInstance Instance, RecordingDialogService Dialogs, ScriptedAuthClient Auth)>
        StartupScenarioAsync()
    {
        var dialogs = new RecordingDialogService();
        var auth = new ScriptedAuthClient { ["cold-refresh"] = Fresh("cold-access") };
        var validator = new MapJwtValidator
        {
            ["cold-access"] = new EsiIdentity(ColdSprockets, "ColdSprockets", ["publicData", "esi-skills.read_skills.v1"]),
        };

        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IDialogService>(dialogs);
            services.AddSingleton<IEsiAuthClient>(auth);
            services.AddSingleton<IEsiJwtValidator>(validator);
        });

        await RegisterRosterAsync(instance);
        await StoreTokenAsync(instance, [Jithran, Abnoba, Noahmarr], DateTimeOffset.UtcNow.AddHours(1));
        await StoreTokenAsync(instance, [ColdSprockets], DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "cold-refresh");
        await StoreTokenAsync(instance, [HotSprockets, LyraCustos], DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: "revoked");

        return (instance, dialogs, auth);
    }

    private static async Task RegisterRosterAsync(TestClientInstance instance)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (var (id, name) in Roster)
            await registry.AddOrUpdateAsync(new Character(name, id, ["publicData"]));
    }

    private static async Task StoreTokenAsync(
        TestClientInstance instance, int[] characterIds, DateTimeOffset expiresAt, string refreshToken = "refresh")
    {
        var store = instance.Services.GetRequiredService<IPerCharacterTokenStore>();
        foreach (var id in characterIds)
            await store.SaveAsync(id, new EsiTokenSet($"access-{id}", refreshToken, expiresAt));
    }

    private static EsiTokenStatusTracker Tracker(TestClientInstance instance) =>
        instance.Services.GetRequiredService<EsiTokenStatusTracker>();

    /// <summary>The startup chain is fire-and-forget from the constructor; the modal is its finish line.</summary>
    private static async Task WaitForStartupCheckAsync(RecordingDialogService dialogs)
    {
        for (var i = 0; i < 200 && dialogs.LastMessage is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Waits for the constructor's startup chain to have checked every character. A test that seeds a status by
    /// hand has to do it after this, or the startup check's own (correct) verdict lands on top of the seed.
    /// </summary>
    private static async Task WaitForStartupCheckAsync(TestClientInstance instance)
    {
        var tracker = Tracker(instance);
        for (var i = 0; i < 300 && Roster.Any(r => tracker.Get(r.Id) is null); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.All(Roster, r => Assert.NotNull(tracker.Get(r.Id)));
    }

    /// <summary>Lets every posted rebuild land: the registry write inside the check queues one on the UI thread.</summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Asserts that exactly these characters warn and every other row reads as a working session.</summary>
    private static void AssertWarning(MainWindowViewModel vm, params int[] expectedWarning)
    {
        var warning = vm.Characters.Where(c => c.EsiWarn).Select(c => c.CharacterId).Order().ToList();
        Assert.Equal(expectedWarning.Order().ToList(), warning);
        foreach (var row in vm.Characters.Where(c => !expectedWarning.Contains(c.CharacterId)))
            Assert.True(row.EsiOk, $"{row.Name} should still read as connected but is '{row.EsiChipText}'.");
    }

    private static IReadOnlyList<(CharacterViewModel Row, Border Border, string Text)> EsiChips(MainWindow window) =>
        window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("chip")
                        && b.Child is TextBlock { Text: not null } t
                        && t.Text.StartsWith("ESI", StringComparison.Ordinal)
                        && b.DataContext is CharacterViewModel)
            .Select(b => ((CharacterViewModel)b.DataContext!, b, ((TextBlock)b.Child!).Text!))
            .ToList();

    private static ClientTokenRefreshService Refresher(
        EsiTokenStatusTracker tracker, IPerCharacterTokenStore store, IEsiAuthClient auth, IEsiJwtValidator validator,
        ICharacterRegistry? registry = null) =>
        new(registry ?? new CountingRegistry(), store, auth, validator, new EsiOptions { ClientId = "test" }, tracker,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientTokenRefreshService>.Instance);

    private static EsiTokenSet Fresh(string accessToken = "access") =>
        new(accessToken, "refresh", DateTimeOffset.UtcNow.AddHours(1));

    private static EsiTokenSet Stale(string refreshToken) =>
        new("stale", refreshToken, DateTimeOffset.UtcNow.AddMinutes(-1));

    // ── Doubles ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>EVE SSO: a refresh token it knows returns the mapped set; anything else is invalid_grant.</summary>
    private sealed class ScriptedAuthClient : IEsiAuthClient
    {
        private readonly Dictionary<string, EsiTokenSet> _byRefreshToken = [];
        private int _refreshCalls;

        public EsiTokenSet this[string refreshToken] { set => _byRefreshToken[refreshToken] = value; }
        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        public Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _refreshCalls);
            return _byRefreshToken.TryGetValue(refreshToken, out var set)
                ? Task.FromResult(set)
                : throw new InvalidOperationException("EVE SSO returned invalid_grant for the refresh token.");
        }

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MapJwtValidator : IEsiJwtValidator
    {
        private readonly Dictionary<string, EsiIdentity> _byAccessToken = [];

        public EsiIdentity this[string accessToken]
        {
            set => _byAccessToken[accessToken] = value;
        }

        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            _byAccessToken.TryGetValue(accessToken, out var identity)
                ? Task.FromResult(identity)
                : throw new InvalidOperationException("ESI access token failed validation.");
    }

    private sealed class MapTokenStore : IPerCharacterTokenStore
    {
        private readonly Dictionary<int, EsiTokenSet> _tokens = [];

        public EsiTokenSet this[int characterId] { set => _tokens[characterId] = value; }

        public Task SaveAsync(int characterId, EsiTokenSet tokens, CancellationToken cancellationToken = default)
        {
            _tokens[characterId] = tokens;
            return Task.CompletedTask;
        }

        public Task<EsiTokenSet?> LoadAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_tokens.GetValueOrDefault(characterId));

        public Task<IReadOnlyList<int>> ListCharacterIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>(_tokens.Keys.ToList());

        public Task RemoveAsync(int characterId, CancellationToken cancellationToken = default)
        {
            _tokens.Remove(characterId);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingRegistry : ICharacterRegistry
    {
        public IReadOnlyList<Character> Characters { get; set; } = [];
        public int Writes { get; private set; }

        public event Action RegistryChanged = () => { };

        public Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default)
        {
            Writes++;
            RegistryChanged();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Characters);

        public Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
