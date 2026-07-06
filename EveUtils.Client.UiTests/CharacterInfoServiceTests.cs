using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Resolving a character's public affiliation through the metered ESI pivot: <c>/characters</c> →
/// <c>/corporations</c> + <c>/alliances</c>, the "Corp [TICK] · Alliance [TICK]" label, the change
/// notification + dedup, and keeping the last good value on a transient failure.
/// </summary>
public class CharacterInfoServiceTests
{
    [Fact]
    public async Task RefreshAsync_ResolvesCorpAndAlliance_BuildsLabel()
    {
        var service = new CharacterInfoService(new EsiAffiliationResolver(StubFor(77, corpId: 1, allyId: 2)));

        var info = await service.RefreshAsync(77, TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal("Test Corp [TST] · Test Alliance [TSA]", info!.AffiliationLabel);
        Assert.Equal(info, service.GetCached(77));
    }

    [Fact]
    public async Task RefreshAsync_NoAlliance_LabelIsCorpOnly()
    {
        var service = new CharacterInfoService(new EsiAffiliationResolver(StubFor(77, corpId: 1, allyId: null)));

        var info = await service.RefreshAsync(77, TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal("Test Corp [TST]", info!.AffiliationLabel);
    }

    [Fact]
    public async Task RefreshAsync_RaisesChanged_OnFirstResolve()
    {
        var service = new CharacterInfoService(new EsiAffiliationResolver(StubFor(77, corpId: 1, allyId: 2)));

        (int Id, CharacterPublicInfo? Info)? raised = null;
        service.AffiliationChanged += (id, info) => raised = (id, info);

        await service.RefreshAsync(77, TestContext.Current.CancellationToken);

        Assert.NotNull(raised);
        Assert.Equal(77, raised!.Value.Id);
        Assert.Equal("TST", raised.Value.Info!.CorporationTicker);
    }

    [Fact]
    public async Task RefreshAsync_DoesNotRaise_WhenUnchanged()
    {
        var service = new CharacterInfoService(new EsiAffiliationResolver(StubFor(77, corpId: 1, allyId: 2)));
        await service.RefreshAsync(77, TestContext.Current.CancellationToken); // first resolve establishes the value

        var raises = 0;
        service.AffiliationChanged += (_, _) => raises++;
        await service.RefreshAsync(77, TestContext.Current.CancellationToken); // identical data → no change

        Assert.Equal(0, raises);
    }

    [Fact]
    public async Task RefreshAsync_KeepsLastGood_OnTransientFailure()
    {
        var client = StubFor(77, corpId: 1, allyId: 2);
        var service = new CharacterInfoService(new EsiAffiliationResolver(client));
        var good = await service.RefreshAsync(77, TestContext.Current.CancellationToken);

        var raises = 0;
        service.AffiliationChanged += (_, _) => raises++;
        client.Responses.Remove("/characters/77/"); // public ESI momentarily can't resolve the character

        var afterFailure = await service.RefreshAsync(77, TestContext.Current.CancellationToken);

        Assert.Equal(good, afterFailure);          // last good value is kept, not blanked
        Assert.Equal(good, service.GetCached(77));
        Assert.Equal(0, raises);                   // no spurious change event
    }

    private static RoutingEsiClient StubFor(int characterId, int corpId, int? allyId)
    {
        var client = new RoutingEsiClient();
        client.Responses[$"/characters/{characterId}/"] =
            new EsiCharacterPublic { Name = "Test Pilot", CorporationId = corpId, AllianceId = allyId };
        client.Responses[$"/corporations/{corpId}/"] =
            new EsiCorporationPublic { Name = "Test Corp", Ticker = "TST" };
        if (allyId is { } id)
            client.Responses[$"/alliances/{id}/"] =
                new EsiAlliancePublic { Name = "Test Alliance", Ticker = "TSA" };
        return client;
    }

    /// <summary>An <see cref="IEsiClient"/> that answers each typed GET from a per-path response table.</summary>
    private sealed class RoutingEsiClient : IEsiClient
    {
        public Dictionary<string, object?> Responses { get; } = new();

        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Responses.TryGetValue(request.Path, out var value) && value is T typed
                ? EsiResult<T>.Ok(typed)
                : EsiResult<T>.Fail(EsiError.Of(EsiErrorKind.ServerError, $"no stub for {request.Path}", 500)));
    }
}
