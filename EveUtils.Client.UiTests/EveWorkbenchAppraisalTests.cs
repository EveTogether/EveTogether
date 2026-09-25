using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Market.Services.Implementations;
using EveUtils.Shared.Modules.Fittings.Services.Implementations;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-364: EVE Workbench as a second appraisal provider, chosen and resolved through
/// <see cref="IAppraisalProviderSelector"/> rather than the last-registered-wins <c>GetService&lt;IAppraisalProvider&gt;()</c>,
/// with its personal access token encrypted at rest and never exposed on a failure. One test per acceptance
/// criterion from the grooming, each a counter-proof that was red against the mechanism it replaces.
/// </summary>
public sealed class EveWorkbenchAppraisalTests
{
    /// <summary>AC1: the persisted choice governs selection, not registration order — the failure mode being
    /// guarded against is <c>GetService&lt;IAppraisalProvider&gt;()</c> silently handing back whichever provider was
    /// registered last once a second one exists.</summary>
    [Fact]
    public async Task Selector_HonorsThePersistedChoice_EvenWhenAnotherProviderRegisteredLast()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IAppraisalProvider>(new StubProvider("stub-other")));
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new SetSettingCommand(AppraisalProviderSelector.SettingKey, "market-prices"), cancellationToken);
        var selector = instance.Services.GetRequiredService<IAppraisalProviderSelector>();

        var chosen = await selector.SelectAsync(cancellationToken);

        Assert.Equal("market-prices", chosen!.Id);
        // The bug this replaces: plain GetService<IAppraisalProvider>() resolves to whatever registered last —
        // "stub-other" here — silently ignoring the setting above.
        Assert.Equal("stub-other", instance.Services.GetRequiredService<IAppraisalProvider>().Id);
    }

    /// <summary>AC2: a real response maps correctly — the token travels in its own header, the estimate is the sell
    /// price (EVE Workbench's own default), and a line its answer carries no matching type id for lands in
    /// Unresolved rather than silently vanishing from the total.</summary>
    [Fact]
    public async Task Provider_MapsARealResponse_HeaderEstimateAndAMissingLine()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HttpRequestMessage? sent = null;
        var provider = new EveWorkbenchAppraisalProvider(
            new StubHttpClientFactory(new StubHandler(request =>
            {
                sent = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"Id":"11111111-1111-1111-1111-111111111111","Items":[{"TypeId":34,"Amount":1000,"Name":"Tritanium","Volume":10,"BuyPrice":4.5,"SellPrice":5.0}],"ItemCount":1,"Error":false,"Message":null}""",
                        Encoding.UTF8, "application/json")
                };
            })),
            new FixedKeyStore("a-personal-access-token"));

        var result = await provider.AppraiseAsync(
            [new AppraisalLine(34, "Tritanium", 1000), new AppraisalLine(35, "Pyerite", 500)], cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("a-personal-access-token", sent!.Headers.GetValues("Character-Access-Token").Single());
        var tritanium = Assert.Single(result.Value!.Rows);
        Assert.Equal(5.0, tritanium.Price!.Estimate);
        Assert.Equal(5.0, tritanium.Price.Sell);
        Assert.Equal(4.5, tritanium.Price.Buy);
        Assert.Equal(["Pyerite"], result.Value.Unresolved);
    }

    /// <summary>AC3: the token is unreadable at rest and a forced 401 (an invalid token, the case most likely to
    /// echo it back) never puts it in the failure text — the raw setting value is ciphertext, not the token, and
    /// the message a caller could show or log names only the HTTP outcome.</summary>
    [Fact]
    public async Task Token_IsEncryptedAtRest_AndNeverLeaksOnAForced401()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string secret = "super-secret-personal-access-token";
        using var instance = TestClientInstance.Create(services => services.AddHttpClient(EveWorkbenchFitClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var keyStore = instance.Services.GetRequiredService<IEveWorkbenchKeyStore>();
        await keyStore.SetTokenAsync(secret, cancellationToken);

        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var settings = await dispatcher.Query(new GetSettingsQuery(), cancellationToken);
        var raw = settings.Single(setting => setting.Key == EveWorkbenchKeyStore.SettingKey).Value;
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);

        var provider = instance.Services.GetRequiredService<IEnumerable<IAppraisalProvider>>()
            .Single(candidate => candidate.Id == "eveworkbench");
        var result = await provider.AppraiseAsync([new AppraisalLine(34, "Tritanium", 1)], cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(secret, result.Messages.Single().Text, StringComparison.Ordinal);
    }

    /// <summary>AC4: an unreachable EVE Workbench falls the run-side valuation back to the ESI average, and the
    /// basis says so — the run, mining and consumables screens' rule, exercised here through the selector they all
    /// go through.</summary>
    [Fact]
    public async Task Selector_FallsBackToEsiAverage_WhenTheChosenProviderFails_AndSaysSo()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create(services => services.AddHttpClient(EveWorkbenchFitClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));
        var keyStore = instance.Services.GetRequiredService<IEveWorkbenchKeyStore>();
        await keyStore.SetTokenAsync("a-token", cancellationToken); // configured, so it is tried and fails, rather than skipped
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new SetSettingCommand(AppraisalProviderSelector.SettingKey, "eveworkbench"), cancellationToken);
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 5, AdjustedPrice = 5, UpdatedAt = DateTimeOffset.UtcNow }], cancellationToken);
        var selector = instance.Services.GetRequiredService<IAppraisalProviderSelector>();

        var result = await selector.AppraiseWithFallbackAsync([new AppraisalLine(34, "Tritanium", 1)], cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value!.Rows.Single().Price!.Estimate);
        Assert.Contains("EVE Workbench unavailable", result.Value.PricingBasis, StringComparison.Ordinal);
    }

    /// <summary>AC5: without a token EVE Workbench is not selectable — the setting still names it, but the
    /// selector falls back to the ESI average instead of choosing a provider it knows cannot answer.</summary>
    [Fact]
    public async Task Selector_FallsBackToMarketPrices_WhenTheChosenProviderHasNoKey()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new SetSettingCommand(AppraisalProviderSelector.SettingKey, "eveworkbench"), cancellationToken);
        var selector = instance.Services.GetRequiredService<IAppraisalProviderSelector>();

        var chosen = await selector.SelectAsync(cancellationToken);

        Assert.Equal("market-prices", chosen!.Id);
    }

    private sealed class StubProvider(string id) : IAppraisalProvider
    {
        public string Id => id;
        public string DisplayName => id;

        public Task<Result<AppraisalOutcome>> AppraiseAsync(
            IReadOnlyCollection<AppraisalLine> lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AppraisalOutcome>.Success(new AppraisalOutcome([], [], id)));
    }

    private sealed class FixedKeyStore(string? token) : IEveWorkbenchKeyStore
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
        public Task SetTokenAsync(string? value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Fixed for the test — not settable.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri(EveWorkbenchFitClient.BaseUrl) };
    }
}
