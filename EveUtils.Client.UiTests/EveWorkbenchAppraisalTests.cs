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
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Market.Services.Implementations;
using EveUtils.Shared.Modules.Fittings.Services.Implementations;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-364: EVE Workbench as a second appraisal provider. Pins its wire format and keeps its personal access
/// token encrypted at rest and out of every failure.</summary>
public sealed class EveWorkbenchAppraisalTests
{
    /// <summary>The token travels in its own header and the estimate is the sell price, EVE Workbench's own default.
    /// A line the answer has no type id for lands in Unresolved instead of vanishing from the total.</summary>
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
        HttpRequestMessage request = Assert.NotNull(sent);
        Assert.Equal("a-personal-access-token", request.Headers.GetValues("Character-Access-Token").Single());
        AppraisalOutcome outcome = Assert.NotNull(result.Value);
        var tritanium = Assert.Single(outcome.Rows);
        var price = Assert.NotNull(tritanium.Price);
        Assert.Equal(5.0, price.Estimate);
        Assert.Equal(5.0, price.Sell);
        Assert.Equal(4.5, price.Buy);
        Assert.Equal(["Pyerite"], outcome.Unresolved);
    }

    /// <summary>The stored setting is ciphertext, not the token. A forced 401, the case most likely to echo the token
    /// back, names only the HTTP outcome in its failure text.</summary>
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
