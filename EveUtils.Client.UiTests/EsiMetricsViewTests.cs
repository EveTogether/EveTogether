using System;
using System.Linq;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// the client ESI-metrics window reads the shared <see cref="IEsiRateLimitMonitor"/> (the per-bucket
/// call/error counters fed by the rate-limit handler) and shows a row per bucket. Drives the real view-model over the
/// real monitor, seeds a few calls, and asserts the derived counters plus that the window renders headless.
/// </summary>
public class EsiMetricsViewTests
{
    [AvaloniaFact]
    public void EsiMetrics_ShowsPerBucketCounters_AndRenders()
    {
        using var instance = TestClientInstance.Create();
        var monitor = instance.Services.GetRequiredService<IEsiRateLimitMonitor>();

        // Authed bucket: an OK call on /characters/{id}/ and a 420 (error-limit hit) on /characters/{id}/skills/.
        // Public bucket: one OK call on /status/.
        monitor.RecordBucket("app:100", "/characters/{id}/", new EsiRateLimitHeaders(98, DateTimeOffset.UtcNow.AddSeconds(60), "char", 150, 149, 1, null), 200);
        monitor.RecordBucket("app:100", "/characters/{id}/skills/", new EsiRateLimitHeaders(15, DateTimeOffset.UtcNow.AddSeconds(50), "char", 150, 148, 2, null), 420);
        monitor.RecordBucket("ip", "/status/", new EsiRateLimitHeaders(100, DateTimeOffset.UtcNow.AddSeconds(60), null, null, null, null, null), 200);

        using var vm = new EsiMetricsViewModel(
            monitor,
            instance.Services.GetRequiredService<ICharacterRegistry>(),
            instance.Services.GetRequiredService<ICharacterPortraitProvider>());

        Assert.Equal(2, vm.Count);
        var authed = vm.Buckets.Single(b => b.Key == "app:100");
        Assert.Equal(2, authed.Calls);
        Assert.Equal(1, authed.Successes);
        Assert.Equal(1, authed.Failures);
        Assert.Equal("50%", authed.ErrorRateText);
        Assert.Equal("1/0", authed.LimitHitsText);        // one 420, no 429
        Assert.Equal("148/150", authed.BucketRemainingText); // latest headers win (the 420 call)
        Assert.Equal("15", authed.ErrorRemainingText);

        // Per-endpoint breakdown: the two authed calls split across two endpoint rows.
        Assert.Equal(2, authed.EndpointCount);
        var skills = authed.Endpoints.Single(e => e.Endpoint == "/characters/{id}/skills/");
        Assert.Equal(1, skills.Calls);
        Assert.Equal(1, skills.Failures);
        Assert.Equal("100%", skills.ErrorRateText);
        Assert.Equal("char", skills.GroupText);
        Assert.Equal(420, skills.LastStatus);

        var window = new EsiMetricsWindow(vm) { Width = 680, Height = 520 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-esi-metrics.png");
    }

    [AvaloniaFact]
    public void ExpandedBucket_SurvivesRefresh_AndRefreshesInPlace()
    {
        var bucket = new EsiBucketState("app:100") { Calls = 1, Successes = 1 };
        var row = new EsiBucketRowViewModel(bucket) { IsExpanded = true };

        // A later poll sees more calls on the same bucket: the row updates in place, keeping its expanded state.
        bucket.Calls = 5;
        bucket.Failures = 2;
        row.Update(bucket);

        Assert.True(row.IsExpanded);   // accordion stays open across the metrics poll
        Assert.Equal(5, row.Calls);    // and reflects the fresh counters
        Assert.Equal(2, row.Failures);
    }

    [Fact]
    public void AuthedBucket_ParsesCharacterId_AndShowsResolvedNameWithIdTooltip()
    {
        var row = new EsiBucketRowViewModel(new EsiBucketState("app:883434905"));

        // Before the name resolves: "character:{id}" instead of the raw "app:{id}".
        Assert.True(row.IsCharacter);
        Assert.Equal(883434905, row.CharacterId);
        Assert.Equal("character:883434905", row.DisplayName);
        Assert.Equal("character:883434905", row.IdentityTooltip);

        // Once resolved: the name is the label, the tooltip carries name + character id, the glyph follows the name.
        row.ApplyCharacterIdentity("RaymondKrah", portrait: null);
        Assert.Equal("RaymondKrah", row.DisplayName);
        Assert.Equal("RaymondKrah\ncharacter:883434905", row.IdentityTooltip);
        Assert.Equal("R", row.Initial);
    }

    [Fact]
    public void PublicBucket_IsNotACharacter_AndKeepsItsKey()
    {
        var row = new EsiBucketRowViewModel(new EsiBucketState("ip"));

        Assert.False(row.IsCharacter);
        Assert.Null(row.CharacterId);
        Assert.Equal("ip", row.DisplayName);
    }
}
