using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Esi.Status;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The Tranquility status feature: maps an ESI <c>/status/</c> outcome onto the bottom-bar snapshot
/// (online / VIP / offline / unknown) and drives the poller's change notification + dedup.
/// </summary>
public class EveServerStatusTests
{
    [Fact]
    public void From_Online_WhenStatusReturnsPlayers()
    {
        var snapshot = EveServerStatusSnapshot.From(
            EsiResult<EveServerStatusResponse>.Ok(new EveServerStatusResponse { Players = 24512 }));

        Assert.Equal(EveServerState.Online, snapshot.State);
        Assert.Equal(24512, snapshot.Players);
    }

    [Fact]
    public void From_Vip_WhenVipFlagSet()
    {
        var snapshot = EveServerStatusSnapshot.From(
            EsiResult<EveServerStatusResponse>.Ok(new EveServerStatusResponse { Players = 5, Vip = true }));

        Assert.Equal(EveServerState.Vip, snapshot.State);
    }

    [Fact]
    public void From_Offline_WhenServerError()
    {
        // 503 during downtime maps to ServerError (5xx) → Tranquility is down.
        var snapshot = EveServerStatusSnapshot.From(
            EsiResult<EveServerStatusResponse>.Fail(EsiError.Of(EsiErrorKind.ServerError, "down", 503)));

        Assert.Equal(EveServerState.Offline, snapshot.State);
        Assert.Null(snapshot.Players);
    }

    [Fact]
    public void From_Unknown_WhenOurConnectionFails()
    {
        // A timeout/network failure is about us, not Tranquility — don't claim the server is offline.
        var snapshot = EveServerStatusSnapshot.From(
            EsiResult<EveServerStatusResponse>.Fail(EsiError.Of(EsiErrorKind.Timeout, "gateway", 504)));

        Assert.Equal(EveServerState.Unknown, snapshot.State);
    }

    [Fact]
    public async Task PollOnce_UpdatesCurrent_AndRaisesChanged()
    {
        var client = new StubEsiClient(
            EsiResult<EveServerStatusResponse>.Ok(new EveServerStatusResponse { Players = 24512 }));
        var service = new EveServerStatusService(client, new EsiAvailabilityState(), new EsiOutageDetector(new EsiAvailabilityState()), NullLogger<EveServerStatusService>.Instance);

        EveServerStatusSnapshot? raised = null;
        service.Changed += s => raised = s;

        var snapshot = await service.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EveServerState.Online, snapshot.State);
        Assert.Equal(snapshot, service.Current);
        Assert.Equal(snapshot, raised);
    }

    [Fact]
    public async Task PollOnce_DoesNotRaise_WhenUnchanged()
    {
        var client = new StubEsiClient(
            EsiResult<EveServerStatusResponse>.Ok(new EveServerStatusResponse { Players = 100 }));
        var service = new EveServerStatusService(client, new EsiAvailabilityState(), new EsiOutageDetector(new EsiAvailabilityState()), NullLogger<EveServerStatusService>.Instance);

        await service.PollOnceAsync(TestContext.Current.CancellationToken); // first poll establishes the snapshot

        var raises = 0;
        service.Changed += _ => raises++;
        await service.PollOnceAsync(TestContext.Current.CancellationToken); // identical result → no change

        Assert.Equal(0, raises);
    }

    [Fact]
    public async Task PollOnce_FlagsMaintenance_WhenStatusFails()
    {
        var availability = new EsiAvailabilityState();
        var client = new StubEsiClient(
            EsiResult<EveServerStatusResponse>.Fail(EsiError.Of(EsiErrorKind.ServerError, "down", 503)));
        var service = new EveServerStatusService(client, availability, new EsiOutageDetector(availability), NullLogger<EveServerStatusService>.Instance);

        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(availability.IsUsable); // a failed /status/ poll gates the rest of the app's ESI calls
    }

    [Fact]
    public async Task PollOnce_ResetsTheOutageDetector_OnRecovery()
    {
        var availability = new EsiAvailabilityState();
        var detector = new RecordingEsiOutageDetector();
        var client = new SequenceEsiClient(
            EsiResult<EveServerStatusResponse>.Fail(EsiError.Of(EsiErrorKind.ServerError, "down", 503)),
            EsiResult<EveServerStatusResponse>.Ok(new EveServerStatusResponse { Players = 42 }));
        var service = new EveServerStatusService(client, availability, detector, NullLogger<EveServerStatusService>.Instance);

        await service.PollOnceAsync(TestContext.Current.CancellationToken); // down → Maintenance, nothing to reset yet
        Assert.Equal(0, detector.Resets);

        await service.PollOnceAsync(TestContext.Current.CancellationToken); // back up → recovery clears the failure run
        Assert.True(availability.IsUsable);
        Assert.Equal(1, detector.Resets);
    }

    [Fact]
    public async Task PollOnce_HandlesFlapping_WithoutThrash()
    {
        var availability = new EsiAvailabilityState();
        var detector = new RecordingEsiOutageDetector();
        var ok = EsiResult<EveServerStatusResponse>.Ok(new EveServerStatusResponse { Players = 7 });
        var down = EsiResult<EveServerStatusResponse>.Fail(EsiError.Of(EsiErrorKind.ServerError, "down", 503));
        var client = new SequenceEsiClient(ok, down, ok, down, ok); // up → down → up → down → up
        var service = new EveServerStatusService(client, availability, detector, NullLogger<EveServerStatusService>.Instance);

        var states = new System.Collections.Generic.List<bool>();
        for (var i = 0; i < 5; i++)
        {
            await service.PollOnceAsync(TestContext.Current.CancellationToken);
            states.Add(availability.IsUsable);
        }

        Assert.Equal(new[] { true, false, true, false, true }, states); // availability tracks each flip exactly
        Assert.Equal(2, detector.Resets); // one reset per recovery (down→up), no spurious resets while up or down
    }

    /// <summary>An <see cref="IEsiClient"/> that returns a fixed outcome for the typed GET under test.</summary>
    private sealed class StubEsiClient(EsiResult<EveServerStatusResponse> result) : IEsiClient
    {
        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult((EsiResult<T>)(object)result);
    }

    /// <summary>An <see cref="IEsiClient"/> that returns each given outcome in turn, holding the last one after.</summary>
    private sealed class SequenceEsiClient(params EsiResult<EveServerStatusResponse>[] results) : IEsiClient
    {
        private int _index;

        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default)
        {
            var result = results[System.Math.Min(_index, results.Length - 1)];
            _index++;
            return Task.FromResult((EsiResult<T>)(object)result);
        }
    }
}
