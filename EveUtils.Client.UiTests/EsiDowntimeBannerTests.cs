using EveUtils.Client.Esi;
using EveUtils.Shared.Modules.Esi.Status;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The downtime banner is availability-driven so it stays consistent with the gate: it shows whenever
/// non-essential calls are withheld — including a timeout/unreachable downtime (Unknown), not only a 5xx.
/// </summary>
public class EsiDowntimeBannerTests
{
    [Fact]
    public void Banner_ShowsMaintenance_WhenGatedAndOffline()
    {
        var (show, message) = EsiDowntimeBanner.For(esiUsable: false, EveServerState.Offline);

        Assert.True(show);
        Assert.Contains("maintenance", message);
    }

    [Fact]
    public void Banner_ShowsUnreachable_WhenGatedButNotConfirmedOffline()
    {
        // The case this refinement fixes: a timeout/unreachable downtime maps to Unknown, yet the gate is active.
        var (show, message) = EsiDowntimeBanner.For(esiUsable: false, EveServerState.Unknown);

        Assert.True(show);
        Assert.Contains("Can't reach ESI", message);
    }

    [Fact]
    public void Banner_ShowsVip_WhenUsableButLimited()
    {
        var (show, message) = EsiDowntimeBanner.For(esiUsable: true, EveServerState.Vip);

        Assert.True(show);
        Assert.Contains("VIP", message);
    }

    [Theory]
    [InlineData(EveServerState.Online)]
    [InlineData(EveServerState.Unknown)] // usable + unknown = pre-poll/idle → no banner (no startup flash)
    public void Banner_Hidden_WhenUsable(EveServerState state)
    {
        var (show, _) = EsiDowntimeBanner.For(esiUsable: true, state);

        Assert.False(show);
    }
}
