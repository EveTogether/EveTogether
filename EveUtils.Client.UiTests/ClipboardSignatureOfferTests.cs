using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="ClipboardSignatureOffer"/> (ET-79): what the toast says about a copied scan signature, built only
/// from what the SDE actually carries.
/// </summary>
public sealed class ClipboardSignatureOfferTests
{
    [AvaloniaFact]
    public async Task UnmatchedSignatureName_StatesEnglishOnlyMatching_NotThatTheSiteIsMissing()
    {
        using var env = await Env.StartAsync();

        env.Copy("KDC-304\tCosmic Signature\tCombat Site\tRuined Blood Raider Crystal Quarry\t100.0%\t2.71 AU");

        var message = Assert.Single(env.Toasts.ActionToasts).Message;
        Assert.Contains("matched in English only", message);
        Assert.DoesNotContain("not in the site catalogue", message);
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
        const string text = "AAA-001\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU";

        env.Copy(text);
        env.Copy(text); // same payload, card still open — must not stack a second card
        Assert.Single(env.Toasts.ActionToasts);

        env.Toasts.ActionToasts[0].Actions[0].Run(); // "Close"
        env.Copy(text);
        Assert.Equal(2, env.Toasts.ActionToasts.Count);
    }

    private static SdeSite Site(int dungeonId, string name, string? archetype = null, string? faction = null, int? ded = null, bool restricted = false) =>
        new(dungeonId, name, archetype is null ? null : 1, archetype, faction is null ? null : 1, faction, null, ded, restricted, []);

    private sealed class Env : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly ClipboardWatchService _watch;
        private readonly ClipboardSignatureOffer _offer;
        private readonly FakeClipboardChangeSource _source;

        public RecordingToastService Toasts { get; } = new();

        public FakeSdeAccessor Sde { get; } = new();

        private Env(TestClientInstance instance, ClipboardWatchService watch, FakeClipboardChangeSource source)
        {
            _instance = instance;
            _watch = watch;
            _source = source;
            _offer = new ClipboardSignatureOffer(watch, Toasts, Sde);
        }

        public static async Task<Env> StartAsync()
        {
            var source = new FakeClipboardChangeSource();
            var instance = TestClientInstance.Create();
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
