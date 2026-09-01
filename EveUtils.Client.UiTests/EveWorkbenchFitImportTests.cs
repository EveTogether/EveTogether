using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Services.Implementations;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;
using EveUtils.Shared.Modules.Sde.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// EVE Workbench fit import: reading the fit id out of every link shape, the client's handling of the
/// public API's answers, and the EFT block it returns landing in the internal model with its subsystems.
/// </summary>
public class EveWorkbenchFitImportTests
{
    private const string FitId = "1044d1c4-33da-4f21-aa37-5c0aa436a524";

    [Theory]
    [InlineData("https://eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524")]
    [InlineData("https://eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524/")]
    [InlineData("https://eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524?tab=stats")]
    [InlineData("https://eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524#modules")]
    [InlineData("https://eveworkbench.com/fit/miasmos/1044d1c4-33da-4f21-aa37-5c0aa436a524")]
    [InlineData("https://eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524/miasmos")]
    [InlineData("http://eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524")]
    [InlineData("https://www.eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524")]
    [InlineData("eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524")]
    [InlineData("https://eveworkbench.com/fit/1044D1C4-33DA-4F21-AA37-5C0AA436A524")]
    [InlineData("1044d1c4-33da-4f21-aa37-5c0aa436a524")]
    [InlineData("  https://eveworkbench.com/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524\n")]
    public void TryParseFitId_SupportedLinkShape_YieldsTheSameFitId(string input)
    {
        Assert.True(EveWorkbenchFitUrl.TryParseFitId(input, out var fitId));
        Assert.Equal(Guid.Parse(FitId), fitId);
    }

    [Theory]
    [InlineData("https://evil.example/fit/1044d1c4-33da-4f21-aa37-5c0aa436a524")] // foreign host carrying a GUID
    [InlineData("https://eveship.fit/?fit=v3:H4sIAAAA")]
    [InlineData("https://eveworkbench.com/fits")]
    [InlineData("[Rifter, My Rifter]\nDamage Control II")]
    [InlineData("587:2048;1::")]
    [InlineData("")]
    public void TryParseFitId_NotAWorkbenchFitLink_Refuses(string input)
    {
        Assert.False(EveWorkbenchFitUrl.TryParseFitId(input, out var fitId));
        Assert.Equal(Guid.Empty, fitId);
    }

    [Fact]
    public void IsEveWorkbenchLink_WorkbenchPageWithoutFitId_IsStillRecognised()
    {
        // Drives the "link, but no fit id" message; a foreign host must not reach it.
        Assert.True(EveWorkbenchFitUrl.IsEveWorkbenchLink("https://eveworkbench.com/fits"));
        Assert.False(EveWorkbenchFitUrl.IsEveWorkbenchLink("https://evil.example/eveworkbench.com/fit"));
    }

    [Fact]
    public async Task FetchEftAsync_PublishedFit_ReturnsTheEftBlock()
    {
        var client = ClientReturning(HttpStatusCode.OK,
            """{"Eft":"[Miasmos, Ore Hauler]\nDamage Control II\n","Error":false,"Message":null}""");

        var result = await client.FetchEftAsync(Guid.Parse(FitId), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("[Miasmos, Ore Hauler]", result.Value);
    }

    [Fact]
    public async Task FetchEftAsync_UnknownOrPrivateFit_FailsDespiteHttp200()
    {
        // The live API answers 200 with Error=true for a fit that does not exist or is not published, so
        // trusting the status code would import an empty fit without telling anyone.
        var client = ClientReturning(HttpStatusCode.OK,
            """{"Eft":null,"Error":true,"Message":"Invalid fittingId received"}""");

        var result = await client.FetchEftAsync(Guid.Parse(FitId), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.NotFound, result.Messages.Single().Code);
    }

    [Fact]
    public async Task FetchEftAsync_ErrorFlagSetAlongsideContent_StillFails()
    {
        // The Error flag decides, not the presence of a body: an EFT block that arrives with Error=true is
        // not a fit we may store. Only this case separates the flag check from the empty-Eft check.
        var client = ClientReturning(HttpStatusCode.OK,
            """{"Eft":"[Miasmos, Ore Hauler]\n","Error":true,"Message":"Invalid fittingId received"}""");

        var result = await client.FetchEftAsync(Guid.Parse(FitId), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task FetchEftAsync_UnexpectedStatus_FailsWithAReadableMessage()
    {
        var client = ClientReturning(HttpStatusCode.BadGateway, "");

        var result = await client.FetchEftAsync(Guid.Parse(FitId), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("502", result.Messages.Single().Text);
    }

    [Fact]
    public async Task FetchEftAsync_HostUnreachable_FailsInsteadOfThrowing()
    {
        var client = ClientThrowing(new HttpRequestException("no such host"));

        var result = await client.FetchEftAsync(Guid.Parse(FitId), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.ServerError, result.Messages.Single().Code);
    }

    [Fact]
    public async Task FetchEftAsync_Timeout_FailsInsteadOfThrowing()
    {
        var client = ClientThrowing(new TaskCanceledException("timed out"));

        var result = await client.FetchEftAsync(Guid.Parse(FitId), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.Timeout, result.Messages.Single().Code);
    }

    [Fact]
    public void Import_WorkbenchEftForATechThreeCruiser_KeepsItsSubsystems()
    {
        // Verbatim from https://api.eveworkbench.com/v1/fits/633aedce-1c46-4dbb-82db-3fcaf18044fc/eft (2026-08-24).
        // The JSON fit endpoint has no subsystem field at all, so this is the regression that route would cause.
        var eft = string.Join("\n",
            "[Proteus, [FOB] Proteus]",
            "Nanofiber Internal Structure II",
            "",
            "500MN Y-T8 Compact Microwarpdrive",
            "",
            "Covert Ops Cloaking Device II",
            "",
            "Medium Polycarbon Engine Housing II",
            "",
            "Proteus Core - Augmented Fusion Reactor",
            "Proteus Defensive - Covert Reconfiguration",
            "Proteus Offensive - Drone Synthesis Projector",
            "Proteus Propulsion - Localized Injectors",
            "");

        var result = new FitTextImporter(TechThreeSde()).Import(eft);

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Warnings);
        var subsystemFlags = result.Fit!.Items
            .Where(i => i.Flag.StartsWith("SubSystemSlot", StringComparison.Ordinal))
            .Select(i => i.Flag)
            .OrderBy(f => f)
            .ToArray();
        Assert.Equal(new[] { "SubSystemSlot0", "SubSystemSlot1", "SubSystemSlot2", "SubSystemSlot3" }, subsystemFlags);
        Assert.Equal(29988, result.Fit.ShipTypeId);
        Assert.Contains(result.Fit.Items, i => i.TypeId == 11578 && i.Flag == "HiSlot0");
    }

    private static FakeSdeAccessor TechThreeSde() => new FakeSdeAccessor()
        .Add(29988, "Proteus", 963, 6)
        .Add(1405, "Nanofiber Internal Structure II", 77, 7, SdeSlotType.Low)
        .Add(12052, "500MN Y-T8 Compact Microwarpdrive", 46, 7, SdeSlotType.Medium)
        .Add(11578, "Covert Ops Cloaking Device II", 330, 7, SdeSlotType.High)
        .Add(31059, "Medium Polycarbon Engine Housing II", 773, 7, SdeSlotType.Rig)
        .Add(45591, "Proteus Core - Augmented Fusion Reactor", 964, 32, SdeSlotType.Subsystem)
        .Add(45601, "Proteus Defensive - Covert Reconfiguration", 966, 32, SdeSlotType.Subsystem)
        .Add(45611, "Proteus Offensive - Drone Synthesis Projector", 968, 32, SdeSlotType.Subsystem)
        .Add(45621, "Proteus Propulsion - Localized Injectors", 970, 32, SdeSlotType.Subsystem);

    private static EveWorkbenchFitClient ClientReturning(HttpStatusCode status, string json) =>
        new(new StubHttpClientFactory(new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        })));

    private static EveWorkbenchFitClient ClientThrowing(Exception exception) =>
        new(new StubHttpClientFactory(new StubHandler(_ => throw exception)));

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
