using EveUtils.Client.Fleet;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// One reason per fleet member (ET-440): "not sharing a system" only for a pilot who actually keeps it to themselves,
/// never for one whose client is closed, not connected, too old to say, or who simply has no system read yet.
/// </summary>
public class FleetMemberStatusTests
{
    private const int Owner = 95000001;
    private const long FleetId = 26;

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 19, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset JustNow = Now.AddSeconds(-2);
    private static readonly DateTimeOffset LongAgo = Now - FleetMemberPresence.SilentAfter - TimeSpan.FromSeconds(1);

    private static FleetMemberStatusReason Read(
        bool? isConnected = true,
        DateTimeOffset? lastHeardAt = null,
        FleetMemberPresenceState presence = FleetMemberPresenceState.Online,
        SharedMetrics? shares = SharedMetrics.Location,
        string? system = null) =>
        FleetMemberStatus.Read(isConnected, lastHeardAt, presence, shares, system, Now);

    [Fact]
    public void Reporting_WithASystem_IsInSystem() =>
        Assert.Equal(FleetMemberStatusReason.InSystem, Read(lastHeardAt: JustNow, system: "Amarr"));

    [Fact]
    public void Reporting_WithLocationOff_IsWithheld() =>
        Assert.Equal(FleetMemberStatusReason.LocationWithheld, Read(lastHeardAt: JustNow, shares: SharedMetrics.Mining));

    [Fact]
    public void Reporting_SharingLocation_ButNoSystemYet_IsNotCalledWithheld() =>
        Assert.Equal(FleetMemberStatusReason.NoSystemYet, Read(lastHeardAt: JustNow));

    [Fact]
    public void Reporting_WithoutAManifest_IsAnOldClient() =>
        Assert.Equal(FleetMemberStatusReason.OldClient, Read(lastHeardAt: JustNow, shares: null));

    [Fact]
    public void Reporting_NotInGame_IsNotInGame_EvenWithAStaleSystem() =>
        Assert.Equal(FleetMemberStatusReason.NotInGame,
            Read(lastHeardAt: JustNow, presence: FleetMemberPresenceState.Offline, system: "Amarr"));

    [Fact]
    public void GoneQuiet_IsSilent_UnlessTheServerSaysNotConnected()
    {
        Assert.Equal(FleetMemberStatusReason.Silent, Read(lastHeardAt: LongAgo, system: "Amarr"));
        Assert.Equal(FleetMemberStatusReason.NotConnected, Read(isConnected: false, lastHeardAt: LongAgo));
    }

    [Fact]
    public void NeverHeard_IsNotConnected_OnlyWhenTheServerSaysSo()
    {
        Assert.Equal(FleetMemberStatusReason.NotConnected, Read(isConnected: false));
        Assert.Equal(FleetMemberStatusReason.NeverHeard, Read(isConnected: true));
        Assert.Equal(FleetMemberStatusReason.NeverHeard, Read(isConnected: null));
    }

    [Fact]
    public async Task Publisher_SendsTheShareManifest_EvenWhenLocationIsNotShared()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        List<MetricSample> broadcast = [];
        var bus = new InProcessEventBus(new RecordingTransport(broadcast));
        var participation = new FleetParticipation();
        participation.Set([new FleetParticipant(Owner, FleetId, ClientOnly: false)]);
        var share = instance.Services.GetRequiredService<IMetricShareSettings>();
        var publisher = new FleetMetricPublisher(participation, [new FixedMetricSource(MetricKind.Location)], bus, share,
            instance.Services.GetRequiredService<FleetMemberActivityTracker>());

        await publisher.PublishTickAsync(1, cancellationToken);
        Assert.DoesNotContain(broadcast, sample => sample.Kind is MetricKind.Location);
        Assert.False(_ManifestOf(broadcast).HasFlag(SharedMetrics.Location));

        using (var scope = instance.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Send(new SetSettingCommand(MetricShareSnapshot.KeyFor(MetricKind.Location), "true"), cancellationToken);
        broadcast.Clear();
        await publisher.PublishTickAsync(2, cancellationToken);
        Assert.True(_ManifestOf(broadcast).HasFlag(SharedMetrics.Location));
    }

    private static SharedMetrics _ManifestOf(List<MetricSample> broadcast) =>
        (SharedMetrics)(int)Assert.Single(broadcast, sample => sample.Kind is MetricKind.Shares).Value;

    private sealed class RecordingTransport(List<MetricSample> broadcast) : IRemoteEventTransport
    {
        public Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            if (integrationEvent is FleetMetricEvent metric)
                broadcast.Add(metric.Data);
            return Task.CompletedTask;
        }
    }
}
