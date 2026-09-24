using System.Net;
using System.Net.Http;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Characters;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Messaging;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Gamelog.Entities;
using EveUtils.Shared.Modules.Implants.Entities;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-345: "Remove character" in the character settings. Driven through the dialog's view model the way a click is,
/// against the real client composition on a scratch data folder. Every HTTP call lands on <see cref="FakeCcp"/>, so
/// the CCP revoke is observed without leaving the machine.
/// </summary>
public sealed class CharacterRemovalTests
{
    private const string Server = "eve.example:7443";
    private const int Removed = 90382598;
    private const string RemovedName = "Abnoba Auscent";
    private const int Kept = 90000001;
    private const string KeptName = "Jithran";

    [AvaloniaFact]
    public async Task Remove_DeletesTheCharacterItsTokenFilesAndItsCaches_AndRevokesItAtCcp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        await harness.SeedCharacterAsync(Kept, KeptName, ct);
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);
        var closed = false;
        dialog.CloseRequested += () => closed = true;

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);
        await dialog.ConfirmRemovalCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.DoesNotContain(harness.Owner.Characters, c => c.CharacterId == Removed);
        Assert.Contains(harness.Owner.Characters, c => c.CharacterId == Kept);
        Assert.Equal([Kept], (await harness.Registry.GetAllAsync(ct)).Select(c => c.EsiCharacterId ?? 0));
        Assert.Null(await harness.Tokens.LoadAsync(Removed, ct));
        Assert.NotNull(await harness.Tokens.LoadAsync(Kept, ct));
        Assert.False(File.Exists(Path.Combine(harness.Instance.DataDirectory, $"esi-{Removed}.bin")));
        Assert.Equal(CharacterRows.Empty, await harness.CountRowsAsync(Removed, RemovedName, ct));
        Assert.Equal(CharacterRows.Seeded, await harness.CountRowsAsync(Kept, KeptName, ct));
        Assert.Equal([$"{Kept}_128.png"], harness.PortraitFiles());

        HttpRequestMessage revoke = Assert.Single(harness.Ccp.Requests);
        Assert.Equal(HttpMethod.Post, revoke.Method);
        Assert.Equal(EsiEndpoints.Revoke, revoke.RequestUri?.ToString());
        Assert.Null(revoke.Headers.Authorization);
        Assert.Equal($"token=refresh-{Removed}&token_type_hint=refresh_token&client_id={harness.ClientId}",
            harness.Ccp.Bodies.Single());
    }

    [AvaloniaFact]
    public async Task Remove_KeepsTheCharactersRunsAndFittings_ByDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        await harness.SeedCharacterAsync(Kept, KeptName, ct);
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);
        await dialog.ConfirmRemovalCommand.ExecuteAsync(null);

        Assert.Equal(1, await harness.CountLiveRunsAsync(Removed, ct));
        Assert.Equal(1, await harness.CountFitsAsync(Removed, ct));
    }

    [AvaloniaFact]
    public async Task Remove_WithTheBoxTicked_AlsoDeletesItsRunsAndFittings_AndNobodyElses()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        await harness.SeedCharacterAsync(Kept, KeptName, ct);
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);
        dialog.DeleteRunsAndFittings = true;
        await dialog.ConfirmRemovalCommand.ExecuteAsync(null);

        Assert.Equal(0, await harness.CountLiveRunsAsync(Removed, ct));
        Assert.Equal(0, await harness.CountFitsAsync(Removed, ct));
        Assert.Equal(1, await harness.CountLiveRunsAsync(Kept, ct));
        Assert.Equal(1, await harness.CountFitsAsync(Kept, ct));
        Assert.Equal(1, await harness.CountFitsAsync(0, ct));
    }

    [AvaloniaFact]
    public async Task Remove_WhenItsServerIsUnreachable_StillRemoves_QueuesTheDecouple_AndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        harness.ServerRevoker.Outcome = ServerRevokeOutcome.Unreachable;
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);
        await dialog.ConfirmRemovalCommand.ExecuteAsync(null);

        Assert.Empty(await harness.Registry.GetAllAsync(ct));
        Assert.Equal([$"access-{Removed}"], harness.ServerRevoker.Attempts);
        Assert.Equal($"access-{Removed}",
            Assert.Single(await harness.Instance.Services.GetRequiredService<IPendingServerRevokeStore>().ListAsync(Server, ct)).AccessToken);
        Assert.Empty(await harness.Instance.Services.GetRequiredService<IClientSessionStore>().ListServersForCharacterAsync(Removed, ct));
        Assert.Contains("could not be reached", harness.Owner.ActivityStatus);
    }

    [AvaloniaFact]
    public async Task Remove_WhenCcpRefusesTheRevoke_StillRemovesEverythingHere()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        harness.Ccp.RevokeStatus = HttpStatusCode.BadRequest;
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);
        await dialog.ConfirmRemovalCommand.ExecuteAsync(null);

        Assert.Single(harness.Ccp.Requests);
        Assert.Empty(await harness.Registry.GetAllAsync(ct));
        Assert.Null(await harness.Tokens.LoadAsync(Removed, ct));
        Assert.Equal(CharacterRows.Empty, await harness.CountRowsAsync(Removed, RemovedName, ct));
    }

    [AvaloniaFact]
    public async Task RemoveCharacter_WhenItCommandsAnActiveLocalFleet_IsRefusedWithTheWayOut()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        await harness.SeedLocalFleetAsync("Friday roam", Removed, FleetActivation.Active, ct);
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);

        Assert.False(dialog.IsConfirmingRemoval);
        Assert.True(dialog.IsRemovalBlocked);
        Assert.Contains("\"Friday roam\"", dialog.RemovalBlockedReason);
        Assert.Contains("Hand the fleet over or stop it first", dialog.RemovalBlockedReason);
        Assert.Single(await harness.Registry.GetAllAsync(ct));
    }

    [AvaloniaFact]
    public async Task RemoveCharacter_WhenItCommandsAnActiveServerFleet_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        harness.Fleets.MyFleetsByServer[Server] =
        [
            new FleetInfo(7, "Corp ops", null, FleetVisibility.InviteOnly, FleetState.Active, Removed,
                null, null, DateTimeOffset.UtcNow, FleetActivation.Active)
        ];
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);

        Assert.True(dialog.IsRemovalBlocked);
        Assert.Contains("\"Corp ops\"", dialog.RemovalBlockedReason);
    }

    [AvaloniaFact]
    public async Task RemoveCharacter_WhenItsFleetIsStopped_OrItsServerCannotBeAsked_IsNotRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        await harness.SeedLocalFleetAsync("Standing by", Removed, FleetActivation.Forming, ct);
        harness.Fleets.UnreachableServers.Add(Server);
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);

        Assert.False(dialog.IsRemovalBlocked);
        Assert.True(dialog.IsConfirmingRemoval);
    }

    [AvaloniaFact]
    public async Task Remove_WithAnOpenRun_SaysItIsStoppedFirst_AndStopsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        Guid running = await harness.StartRunAsync(Removed, ct);
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);
        Assert.True(dialog.RemovalStopsOpenRun);
        await dialog.ConfirmRemovalCommand.ExecuteAsync(null);

        Assert.Equal(RunState.Stopped, await harness.RunStateAsync(running, ct));
        Assert.Empty(await harness.Registry.GetAllAsync(ct));
    }

    [AvaloniaFact]
    public async Task CancelRemoval_KeepsTheCharacter_AndUnticksTheBox()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        var dialog = await harness.OpenSettingsAsync(Removed, RemovedName);

        await dialog.RemoveCharacterCommand.ExecuteAsync(null);
        dialog.DeleteRunsAndFittings = true;
        dialog.CancelRemovalCommand.Execute(null);

        Assert.False(dialog.IsConfirmingRemoval);
        Assert.False(dialog.DeleteRunsAndFittings);
        Assert.Single(await harness.Registry.GetAllAsync(ct));
        Assert.Empty(harness.Ccp.Requests);
    }

    /// <summary>
    /// A refresh already on its way to EVE SSO when the removal starts would, left alone, save the rotated token — and
    /// with a changed grant the registry row — right back after the removal deleted them.
    /// </summary>
    [AvaloniaFact]
    public async Task Remove_WhileATokenRefreshIsInFlight_WaitsForIt_SoNothingIsWrittenBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var sso = new HeldRefresh();
        using var harness = Harness.Create(services =>
        {
            services.AddSingleton<IEsiAuthClient>(sso);
            services.AddSingleton<IEsiJwtValidator>(new GrantingValidator());
        });
        await harness.SeedCharacterAsync(Removed, RemovedName, ct, expiresIn: TimeSpan.FromMinutes(-1));
        var refresh = harness.Instance.Services.GetRequiredService<ClientTokenRefreshService>().EnsureValidAsync(Removed, ct);
        await sso.Started.Task.WaitAsync(ct);

        var removal = harness.Removal.RemoveAsync(Removed, RemovedName, new HashSet<CharacterDataKind> { CharacterDataKind.Cache }, ct);
        Task first = await Task.WhenAny(removal, Task.Delay(TimeSpan.FromMilliseconds(500), ct));
        sso.Release.SetResult();
        await refresh;
        await removal;

        Assert.NotSame(removal, first);
        Assert.Null(await harness.Tokens.LoadAsync(Removed, ct));
        Assert.Empty(await harness.Registry.GetAllAsync(ct));
        Assert.Equal("refresh-rotated", harness.Ccp.Bodies.Single().Split('&')[0].Split('=')[1]);
    }

    /// <summary>The background passes that write per-character rows, run after the removal: none may bring the
    /// character back, and none may reach out to CCP for it.</summary>
    [AvaloniaFact]
    public async Task AfterRemoval_TheBackgroundPasses_WriteNothingBack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = Harness.Create();
        await harness.SeedCharacterAsync(Removed, RemovedName, ct);
        await harness.Removal.RemoveAsync(Removed, RemovedName, new HashSet<CharacterDataKind> { CharacterDataKind.Cache }, ct);
        harness.Ccp.Requests.Clear();

        var services = harness.Instance.Services;
        Assert.Equal(TokenStatus.NoToken, await services.GetRequiredService<ClientTokenRefreshService>().EnsureValidAsync(Removed, ct));
        await services.GetRequiredService<Skills.SkillRefreshService>().RefreshAllAsync(ct);
        await services.GetRequiredService<ShipFitDetectionService>().RefreshAllAsync(ct);

        Assert.Empty(harness.Ccp.Requests);
        Assert.Empty(await harness.Registry.GetAllAsync(ct));
        Assert.Null(await harness.Tokens.LoadAsync(Removed, ct));
        Assert.Equal(CharacterRows.Empty, await harness.CountRowsAsync(Removed, RemovedName, ct));
    }

    private sealed record CharacterRows(int Skills, int Queue, int Attributes, int Implants, int Killmails,
        int KillmailChildren, int Combat, int Metrics, int Inbox, int Sessions, int Settings)
    {
        public static readonly CharacterRows Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public static readonly CharacterRows Seeded = new(1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 3);
    }

    private sealed class Harness : IDisposable
    {
        private Harness(TestClientInstance instance, FakeCcp ccp, ScriptedServerRevoker serverRevoker,
            RecordingFleetTransportClient fleets)
        {
            Instance = instance;
            Ccp = ccp;
            ServerRevoker = serverRevoker;
            Fleets = fleets;
            Owner = new MainWindowViewModel(instance.Services);
        }

        public TestClientInstance Instance { get; }
        public FakeCcp Ccp { get; }
        public ScriptedServerRevoker ServerRevoker { get; }
        public RecordingFleetTransportClient Fleets { get; }
        public MainWindowViewModel Owner { get; }
        public ICharacterRegistry Registry => Instance.Services.GetRequiredService<ICharacterRegistry>();
        public IPerCharacterTokenStore Tokens => Instance.Services.GetRequiredService<IPerCharacterTokenStore>();
        public CharacterRemovalService Removal => Instance.Services.GetRequiredService<CharacterRemovalService>();
        public string ClientId => Instance.Services.GetRequiredService<EsiOptions>().ClientId;

        public static Harness Create(Action<IServiceCollection>? configure = null)
        {
            var ccp = new FakeCcp();
            var serverRevoker = new ScriptedServerRevoker();
            var fleets = new RecordingFleetTransportClient();
            var instance = TestClientInstance.Create(services =>
            {
                services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => ccp));
                services.AddSingleton<IDialogService>(new RecordingDialogService());
                services.AddSingleton<IServerSessionRevoker>(serverRevoker);
                services.AddSingleton<IRemoteBusConnector>(new FakeRemoteBusConnector());
                services.AddSingleton<IFleetTransportClient>(fleets);
                configure?.Invoke(services);
            });
            return new Harness(instance, ccp, serverRevoker, fleets);
        }

        public async Task<CharacterDialogViewModel> OpenSettingsAsync(int characterId, string name)
        {
            await Owner.RefreshCharactersAsync();
            var dialog = new CharacterDialogViewModel(Owner, new CharacterViewModel(new Character(name, characterId)));
            await dialog.InitializeAsync();
            return dialog;
        }

        public async Task SeedCharacterAsync(int id, string name, CancellationToken ct, TimeSpan? expiresIn = null)
        {
            var services = Instance.Services;
            await Registry.AddOrUpdateAsync(new Character(name, id, ["esi-skills.read_skills.v1"]), ct);
            await Tokens.SaveAsync(id, new EsiTokenSet($"access-{id}", $"refresh-{id}",
                DateTimeOffset.UtcNow + (expiresIn ?? TimeSpan.FromHours(1))), ct);
            await services.GetRequiredService<IClientSessionStore>()
                .SaveAsync(Server, new ClientSessionTokens($"access-{id}", $"server-refresh-{id}", name, id), ct);

            await using (ClientDbContext db = await services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(ct))
            {
                db.Set<CharacterSkill>().Add(new CharacterSkill { CharacterId = id, SkillTypeId = 3300, Level = 5 });
                db.Set<CharacterSkillQueueEntry>().Add(new CharacterSkillQueueEntry { CharacterId = id, QueuePosition = 0, SkillTypeId = 3301, FinishedLevel = 4 });
                db.Set<CharacterAttributes>().Add(new CharacterAttributes { CharacterId = id, Charisma = 20, Intelligence = 20, Memory = 20, Perception = 20, Willpower = 20 });
                db.Set<CharacterImplant>().Add(new CharacterImplant { CharacterId = id, ImplantTypeId = 10209 });
                db.Set<LocalKillmail>().Add(new LocalKillmail
                {
                    CharacterId = id, KillmailId = 123, Hash = "hash", KillmailTimeUtc = DateTime.UtcNow, ImportedAtUtc = DateTime.UtcNow,
                    Attackers = [new LocalKillmailAttacker { CharacterId = id, KillmailId = 123, Ordinal = 0, DamageDone = 100 }],
                    Items = [new LocalKillmailItem { CharacterId = id, KillmailId = 123, Flag = 5, TypeId = 34 }]
                });
                db.Set<CombatSample>().Add(new CombatSample { OwnerId = "local", CharacterId = id, Timestamp = DateTimeOffset.UtcNow, Amount = 120, Target = "Rat" });
                db.Set<CharacterMetricState>().Add(new CharacterMetricState { CharacterName = name, BountyTotal = 1_000_000 });
                db.Set<ClientInboxMessage>().Add(new ClientInboxMessage { ServerMessageId = id, RecipientCharacterId = id, Title = "Invite" });
                db.Set<LocalFitting>().Add(new LocalFitting { OwnerId = id.ToString(), EsiFittingId = 1, Name = "Gila", ShipTypeId = 17715, ContentHash = $"fit-{id}" });
                if (!await db.Set<LocalFitting>().AnyAsync(fit => fit.OwnerId == "0", ct))
                    db.Set<LocalFitting>().Add(new LocalFitting { OwnerId = "0", Name = "Library Gila", ShipTypeId = 17715, ContentHash = "library" });
                await db.SaveChangesAsync(ct);
            }

            var settings = services.GetRequiredService<ISettingRepository>();
            await settings.UpsertAsync(OwnCharacterPickMemory.KeyFor(id), $"{id}", ct);
            await settings.UpsertAsync(MetricShareSnapshot.OverrideKeyFor(42, id, Shared.Modules.Fleet.Metrics.MetricKind.Location), "false", ct);
            await settings.UpsertAsync($"fit-detection.override.{id}.17715", "1", ct);

            var portraits = Path.Combine(Instance.DataDirectory, "character-portraits");
            Directory.CreateDirectory(portraits);
            await File.WriteAllBytesAsync(Path.Combine(portraits, $"{id}_128.png"), [1, 2, 3], ct);

            await services.GetRequiredService<IDispatcher>().Send(new StartRunCommand(id, ActivityKind.Site,
                DateTime.UtcNow.AddHours(-2), 1234, "Homefront", 30000142, $"SIG-{id}"), ct);
            await services.GetRequiredService<IDispatcher>().Send(new StopRunsLeftRunningCommand(DateTime.UtcNow.AddHours(-1)), ct);
        }

        public async Task SeedLocalFleetAsync(string name, int creator, FleetActivation activation, CancellationToken ct)
        {
            await using ClientDbContext db = await Instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(ct);
            db.Set<FleetEntity>().Add(new FleetEntity
            {
                Name = name, CreatorCharacterId = creator, IsClientOnly = true, State = FleetState.Active, Activation = activation,
                CreatedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        public async Task<Guid> StartRunAsync(int characterId, CancellationToken ct)
        {
            Result<Guid> started = await Instance.Services.GetRequiredService<IDispatcher>().Send(new StartRunCommand(
                characterId, ActivityKind.Site, DateTime.UtcNow, 1234, "Homefront", 30000142, "RUN-NOW"), ct);
            return started.Value;
        }

        public async Task<RunState> RunStateAsync(Guid runId, CancellationToken ct)
        {
            await using ClientDbContext db = await Instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(ct);
            return await db.Set<Run>().Where(run => run.Id == runId).Select(run => run.State).SingleAsync(ct);
        }

        public async Task<int> CountLiveRunsAsync(int characterId, CancellationToken ct)
        {
            await using ClientDbContext db = await Instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(ct);
            return await db.Set<Run>().CountAsync(run => run.CharacterId == characterId && !run.DeletedAtUtc.HasValue, ct);
        }

        public async Task<int> CountFitsAsync(int ownerId, CancellationToken ct)
        {
            var owner = ownerId.ToString();
            await using ClientDbContext db = await Instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(ct);
            return await db.Set<LocalFitting>().CountAsync(fit => fit.OwnerId == owner, ct);
        }

        public async Task<CharacterRows> CountRowsAsync(int id, string name, CancellationToken ct)
        {
            var settingKeys = new[]
            {
                OwnCharacterPickMemory.KeyFor(id),
                MetricShareSnapshot.OverrideKeyFor(42, id, Shared.Modules.Fleet.Metrics.MetricKind.Location),
                $"fit-detection.override.{id}.17715"
            };
            await using ClientDbContext db = await Instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(ct);
            var settings = await Instance.Services.GetRequiredService<ISettingRepository>().ListAsync(ct);
            return new CharacterRows(
                await db.Set<CharacterSkill>().CountAsync(s => s.CharacterId == id, ct),
                await db.Set<CharacterSkillQueueEntry>().CountAsync(e => e.CharacterId == id, ct),
                await db.Set<CharacterAttributes>().CountAsync(a => a.CharacterId == id, ct),
                await db.Set<CharacterImplant>().CountAsync(i => i.CharacterId == id, ct),
                await db.Set<LocalKillmail>().CountAsync(k => k.CharacterId == id, ct),
                await db.Set<LocalKillmailAttacker>().CountAsync(a => a.CharacterId == id, ct)
                    + await db.Set<LocalKillmailItem>().CountAsync(i => i.CharacterId == id, ct),
                await db.Set<CombatSample>().CountAsync(s => s.CharacterId == id, ct),
                await db.Set<CharacterMetricState>().CountAsync(m => m.CharacterName == name, ct),
                await db.Set<ClientInboxMessage>().CountAsync(m => m.RecipientCharacterId == id, ct),
                (await Instance.Services.GetRequiredService<IClientSessionStore>().ListServersForCharacterAsync(id, ct)).Count,
                settings.Count(setting => settingKeys.Contains(setting.Key)));
        }

        public IReadOnlyList<string> PortraitFiles() =>
            [.. Directory.EnumerateFiles(Path.Combine(Instance.DataDirectory, "character-portraits")).Select(Path.GetFileName).OfType<string>()];

        public void Dispose() => Instance.Dispose();
    }

    /// <summary>Stands in for every remote host: the revoke answers <see cref="RevokeStatus"/>, anything else 503.</summary>
    private sealed class FakeCcp : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public HttpStatusCode RevokeStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "login.eveonline.com")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            lock (Requests)
                Requests.Add(request);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
                Bodies.Add(body);
            return new HttpResponseMessage(request.RequestUri.ToString() == EsiEndpoints.Revoke ? RevokeStatus : HttpStatusCode.NotFound);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class ScriptedServerRevoker : IServerSessionRevoker
    {
        public ServerRevokeOutcome Outcome { get; set; } = ServerRevokeOutcome.Revoked;
        public List<string> Attempts { get; } = [];

        public Task<ServerRevokeOutcome> RevokeAsync(string serverAddress, string sessionToken, CancellationToken cancellationToken = default)
        {
            Attempts.Add(sessionToken);
            return Task.FromResult(Outcome);
        }
    }

    /// <summary>A refresh that reaches EVE SSO and waits there until the test lets it answer — with a rotated token.</summary>
    private sealed class HeldRefresh : IEsiAuthClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new EsiTokenSet("access-rotated", "refresh-rotated", DateTimeOffset.UtcNow.AddMinutes(20));
        }

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Validates any token, with a grant that differs from the stored one — the case that also rewrites the
    /// registry row after a refresh.</summary>
    private sealed class GrantingValidator : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EsiIdentity(Removed, RemovedName, ["esi-skills.read_skills.v1", "esi-clones.read_implants.v1"]));
    }
}
