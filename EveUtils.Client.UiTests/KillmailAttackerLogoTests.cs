using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-477: the corporation and alliance logo beside an attacker, from the shared portrait provider.</summary>
public sealed class KillmailAttackerLogoTests
{
    [Fact]
    public async Task LoadLogos_AskForCorporationAndAlliance_AtTheTileSize()
    {
        var portraits = new RecordingPortraits();
        var attacker = new KillmailDetailAttackerRowViewModel("Pilot", "", "Garmur", null, 1, 50,
            false, false, false, false, 42, 123, 456, 789);

        await attacker.LoadLogosAsync(portraits);

        Assert.Equal([(456, 32)], portraits.Corporations);
        Assert.Equal([(789, 32)], portraits.Alliances);
        Assert.True(attacker.HasCorporation);
        Assert.True(attacker.HasAlliance);
    }

    [Fact]
    public async Task LoadLogos_NoLogoOnTheServer_KeepsTheTileWithoutAnImage()
    {
        var attacker = new KillmailDetailAttackerRowViewModel("Burner", "", "Sentinel", null, 1, 50,
            false, false, false, true, null, 789, 1000125, 500010);

        await attacker.LoadLogosAsync(new RecordingPortraits());

        Assert.True(attacker.HasCorporation);
        Assert.False(attacker.HasCorporationLogo);
        Assert.True(attacker.HasAlliance);
        Assert.False(attacker.HasAllianceLogo);
    }

    [Fact]
    public async Task LoadLogos_NoCorporationOrAlliance_AsksForNothing()
    {
        var portraits = new RecordingPortraits();
        var attacker = new KillmailDetailAttackerRowViewModel("Unknown", "", "unknown ship", null, 1, 50,
            false, false, false, true, null, null, null);

        await attacker.LoadLogosAsync(portraits);

        Assert.Empty(portraits.Corporations);
        Assert.Empty(portraits.Alliances);
        Assert.False(attacker.HasCorporation);
        Assert.False(attacker.HasAlliance);
    }

    [Fact]
    public async Task Provider_LogoNotFound_ReturnsNull_AndIsNotRequestedAgainThisSession()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eveutils-logo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new NotFoundHandler();
            var provider = new CharacterPortraitProvider(new StubHttpClientFactory(handler), new EmptySettings(), directory);
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;

            Assert.Null(await provider.GetAllianceLogoAsync(99000001, 32, cancellationToken));
            Assert.Null(await provider.GetAllianceLogoAsync(99000001, 32, cancellationToken));
            Assert.Null(await provider.GetCorporationLogoAsync(1000125, 32, cancellationToken));
            Assert.Null(await provider.GetCorporationLogoAsync(1000125, 32, cancellationToken));

            Assert.Equal(["alliances/99000001/logo?size=32", "corporations/1000125/logo?size=32"], handler.Paths);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class RecordingPortraits : ICharacterPortraitProvider
    {
        public List<(int Id, int Size)> Corporations { get; } = [];
        public List<(int Id, int Size)> Alliances { get; } = [];

        public Task<Bitmap?> GetPortraitAsync(int characterId, int size, CancellationToken cancellationToken = default) =>
            Task.FromResult<Bitmap?>(null);

        public Task<Bitmap?> GetCorporationLogoAsync(int corporationId, int size, CancellationToken cancellationToken = default)
        {
            Corporations.Add((corporationId, size));
            return Task.FromResult<Bitmap?>(null);
        }

        public Task<Bitmap?> GetAllianceLogoAsync(int allianceId, int size, CancellationToken cancellationToken = default)
        {
            Alliances.Add((allianceId, size));
            return Task.FromResult<Bitmap?>(null);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://images.evetech.net/") };
    }

    private sealed class EmptySettings : ISettingRepository
    {
        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClientSetting>>([]);
        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.PathAndQuery.TrimStart('/') ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
