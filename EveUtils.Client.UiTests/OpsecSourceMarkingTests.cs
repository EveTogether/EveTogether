using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Opsec;
using EveUtils.Client.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-417: every source OpsecCoverageTests holds to "masked" without a screen test of its own marks exactly the
/// location in the text it builds — the place goes behind the markers, the rest of the line (a time, a type, "on this
/// PC") stays readable. Asserted on the exact string, never with a culture-aware Contains: the markers are format
/// characters a culture comparison ignores, so that would pass with the marking taken out.</summary>
public sealed class OpsecSourceMarkingTests
{
    private const long Pilot = 90000001;
    private const int Jita = 30000142;
    private const int Gila = 17715;
    private static readonly DateTime StartedAtUtc = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public void ActivityRow_MarksSystemSecurityAndUnknownSystemId()
    {
        var facts = new RunRowFacts(new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30001000, "Alkabsi", 0.7)));

        ActivityOverviewRowViewModel known = _Row(30001000, facts);
        ActivityOverviewRowViewModel unknown = _Row(31999999, facts);

        Assert.Equal($"{OpsecText.Mark("Alkabsi")} {OpsecText.Mark("0.7")}", known.SystemLineText);
        Assert.Null(known.SystemTooltip);
        Assert.Equal("—", unknown.SystemLineText);
        Assert.Equal($"Solar system {OpsecText.Mark("31999999")} is not in the static data yet", unknown.SystemTooltip);
    }

    [AvaloniaFact]
    public void RunsActivityPane_MetaText_MarksSystemAndSignature()
    {
        var facts = new RunRowFacts(new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30001000, "Alkabsi", 0.7)));
        ActivityOverviewRowViewModel row = _Row(30001000, facts, signatureGroup: "NKP-364");
        var pane = new RunsActivityPaneViewModel((_, _) => Task.FromResult(new RunsPaneDetail([], 0, null, null)),
            readDelay: TimeSpan.Zero);

        pane.Show(row);

        string day = row.StartedAtLocal.ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        Assert.Equal($"{day} · {OpsecText.Mark("Alkabsi")} {OpsecText.Mark("0.7")} · {OpsecText.Mark("NKP-364")}", pane.MetaText);
    }

    [AvaloniaFact]
    public void RunningGroup_KindText_MarksTheSystem()
    {
        var lane = new RunningLaneViewModel(new Character("Ra Vinter", (int)Pilot), new CharacterFaceViewModel(Pilot, "Ra Vinter"),
            _ => Task.CompletedTask);
        lane.Attach(new RunningRunDto(Guid.NewGuid(), Pilot, ActivityKind.Site, StartedAtUtc, null, "Angel Hideaway", "NKP-364"),
            StartedAtUtc.AddMinutes(5), "Combat Site", "Alkabsi");
        var group = new RunningGroupViewModel("run", _ => Task.CompletedTask);

        group.Show([lane], StartedAtUtc.AddMinutes(5));

        Assert.Equal($"Combat Site · {OpsecText.Mark("Alkabsi")}", group.KindText);
        Assert.Equal(OpsecText.Mark("Angel Hideaway"), group.SiteText);
    }

    [AvaloniaFact]
    public async Task HomeDashboard_IdleText_MarksTheLastSite()
    {
        using var instance = TestClientInstance.Create();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Ra Vinter", (int)Pilot), Ct);
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await _SaveRunAsync(dispatcher, "Angel Hideaway", DateTime.UtcNow.AddMinutes(-20));
        await dispatcher.Send(new RebuildActivitySummariesCommand(), Ct);

        using var home = new HomeDashboardViewModel(instance.Services, HomeNavigation.None, []);
        await home.LoadAsync();

        Assert.NotNull(home.Running);
        Assert.StartsWith($"nothing running · last run {OpsecText.Mark("Angel Hideaway")} ended ", home.Running.IdleText,
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ActivityWindow_ClockHint_MarksTheWaitingAndTheRunningSite()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await _RunOnAnotherSiteAsync(harness);

        await model.ApplySignatureAsync("SUG-270", "Combat Site", "Drone Cluster", []);

        Assert.Equal($"{OpsecText.Mark("Drone Cluster")} is copied and waiting. Save or discard this "
                     + $"{OpsecText.Mark("Sansha Hideaway")} run and it takes over; "
                     + "KEEP drops the copy and puts the clock back on this run.", model.ClockHint);
    }

    [AvaloniaFact]
    public async Task LinkedLossChoices_MarkTheSiteName()
    {
        using TestClientInstance instance = _KillmailInstance();
        (Guid linkedRunId, DateTime otherStartedAtUtc) = await _SeedLinkedLossAsync(instance);
        string expected = $"{OpsecText.Mark("Serpentis Lookout")} · {otherStartedAtUtc.ToLocalTime():d MMM HH:mm}";

        KillmailDetailViewModel killmail = await _LoadKillmailAsync(instance);
        ActivityDetailViewModel detail = await _LoadActivityDetailAsync(instance, linkedRunId);

        Assert.NotNull(killmail.LinkedRun);
        Assert.Equal(expected, Assert.Single(killmail.LinkedRun.OtherRuns).Text);
        LinkedLossViewModel loss = Assert.Single(detail.Sections.OfType<LossDetailSectionViewModel>().Single().Losses);
        Assert.Equal(expected, Assert.Single(loss.OtherRuns).Text);
    }

    [AvaloniaFact]
    public async Task KillmailDetail_MarksSystemRegionSecurityAndTheLinkedSite()
    {
        using TestClientInstance instance = _KillmailInstance();
        await _SeedLinkedLossAsync(instance);

        KillmailDetailViewModel killmail = await _LoadKillmailAsync(instance);

        Assert.Equal($"{OpsecText.Mark("Jita")} · {OpsecText.Mark("The Forge")} · {OpsecText.Mark("0.9")}", killmail.SystemLineText);
        Assert.NotNull(killmail.LinkedRun);
        Assert.Equal($"{OpsecText.Mark("Angel Hideaway")} · Gila", killmail.LinkedRun.SiteText);
    }

    [AvaloniaFact]
    public void HomePilotRow_SystemDetailText_MarksTheSecurity()
    {
        var character = new CharacterViewModel(new Character("Noahmarr", (int)Pilot)) { HasActiveClient = true };
        var row = new HomePilotRowViewModel(character, HomeNavigation.None);

        row.ShowSystem("Hakshma", 0.4);

        Assert.Equal($"{OpsecText.Mark("0.4")} · on this PC", row.SystemDetailText);
    }

    [AvaloniaFact]
    public async Task MissionSections_MarkTheMissionLocation()
    {
        var sde = new FakeSdeAccessor().AddSolarSystem(new SdeSolarSystem(30005040, "Nishah", 0.4));
        RunParameterInput location = new()
        {
            ParameterKey = RunParameterKey.MissionLocation, TypedValue = "Nishah", ObservedAtUtc = StartedAtUtc
        };
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Mission, StartedAtUtc, 9999,
            "Cargo Delivery Objectives", Jita, SiteTypeSource: SiteTypeSource.Mission), Ct);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(8), StartedAtUtc.AddMinutes(9),
            [], [], [], [location]), Ct);
        ActivityOverviewRowDto overviewRow = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery(), Ct)).Value ?? []);

        var detail = new ActivityDetailViewModel(dispatcher, overviewRow.ActivitySummaryId, sde: sde);
        await detail.LoadAsync(Ct);
        var services = new ServiceCollection().AddSingleton<ISdeAccessor>(sde).BuildServiceProvider();
        var window = new ActivityWindowViewModel(ActivityKind.Mission, services) { PendingParameters = [location] };
        window.Refresh(StartedAtUtc);

        Assert.Equal(OpsecText.Mark("Nishah"), detail.Mission().MissionLocationText);
        Assert.Equal("not stated in this capture", detail.Mission().AgentText);
        Assert.Equal(OpsecText.Mark("Nishah"), window.Mission().MissionLocationText);
    }

    [AvaloniaFact]
    public async Task ActivityDetailSection_MarksSiteAndSignature()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await _SaveRunAsync(dispatcher, "Angel Hideaway", StartedAtUtc, signature: "NKP-364");
        ActivityOverviewRowDto overviewRow = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery(), Ct)).Value ?? []);

        var detail = new ActivityDetailViewModel(dispatcher, overviewRow.ActivitySummaryId);
        await detail.LoadAsync(Ct);

        Assert.Equal(OpsecText.Mark("Angel Hideaway"), detail.Activity().SiteText);
        Assert.Equal(OpsecText.Mark("NKP-364"), detail.Activity().SignatureText);
        Assert.Equal("not recognised", detail.Activity().FitText);
    }

    [AvaloniaFact]
    public async Task EscalationRegistered_MarksSiteAndDestination()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Site);
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);
        harness.Dialogs.OnShowEscalationDialog = dialog =>
        {
            dialog.SiteQuery = "Command Relay Outpost";
            dialog.DestinationSystem = "Amamake";
            dialog.RemainingTimeText = "23:57:45";
            dialog.RegisterCommand.Execute(null);
            return Task.FromResult(true);
        };

        await model.Activity().RegisterEscalationCommand.ExecuteAsync(null);

        Assert.Equal($"{OpsecText.Mark("Command Relay Outpost")} · {OpsecText.Mark("Amamake")}",
            model.Activity().EscalationRegisteredText);
    }

    /// <summary><see cref="RecordingDialogService"/> refuses the server picker, so the prompt is read through a proxy
    /// that answers that one call — cancelled, which is the earliest the publish stops. The site text is the row's own,
    /// exactly what the runs overview hands over.</summary>
    [AvaloniaFact]
    public async Task RunPublisher_ServerPrompt_CarriesTheMarkedSite()
    {
        using var instance = TestClientInstance.Create();
        ActivityOverviewRowViewModel row = _Row(Jita, new RunRowFacts(null));
        IDialogService dialogs = DispatchProxy.Create<IDialogService, ServerPromptRecorder>();
        var publisher = new RunPublisher(instance.Services.GetRequiredService<ICqrsDispatcher>(), dialogs,
            new ServiceCollection().AddSingleton<IClientSessionStore>(new TwoServers()).BuildServiceProvider());

        RunPublishOutcome? outcome = await publisher.PublishOneAsync(row.ActivitySummaryId, row.SiteText);

        Assert.Equal("Publish cancelled.", outcome?.Message);
        Assert.Equal([$"Publish '{OpsecText.Mark("Angel Hideaway")}' to which server?"], ((ServerPromptRecorder)dialogs).Prompts);
    }

    [AvaloniaFact]
    public async Task SignatureOffer_Toast_MarksSignatureIdAndName()
    {
        var toasts = new RecordingToastService();
        var source = new FakeClipboardChangeSource();
        using var instance = TestClientInstance.Create();
        using var watch = new ClipboardWatchService(new RecordingDialogService(), instance.Services,
            NullLogger<ClipboardWatchService>.Instance, source, new NullForegroundEveClientReader());
        using var offer = new ClipboardSignatureOffer(watch, toasts, new FakeSdeAccessor(), new RecordingDialogService(),
            instance.Services);
        await watch.SetEnabledAsync(true);

        // Two rows: one on its own goes straight to a run and puts no card up (ET-158).
        source.ClipboardText = "KDC-304\tCosmic Signature\tCombat Site\tHaunted Yard\t100.0%\t2.71 AU\r\n"
                               + "KDC-305\tCosmic Signature\tCombat Site\tAngel Hideaway\t100.0%\t1.10 AU";
        source.RaiseChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal($"{OpsecText.Mark("KDC-304")} · {OpsecText.Mark("Haunted Yard")}\n"
                     + $"{OpsecText.Mark("KDC-305")} · {OpsecText.Mark("Angel Hideaway")}",
            Assert.Single(toasts.ActionToasts).Message);
    }

    /// <summary>The failure path is a settings read that throws — the one thing between a sighting and its toast.</summary>
    [AvaloniaFact]
    public async Task HomefrontLogLine_MarksTheSite()
    {
        const long fleetId = 42;
        var log = new RecordingLoggerProvider();
        using ILoggerFactory logs = LoggerFactory.Create(builder => builder.AddProvider(log));
        using var instance = TestClientInstance.Create();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Jithran", (int)Pilot), Ct);
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter((int)Pilot, "Jithran");
        instance.Services.GetRequiredService<IFleetParticipation>().Set([new FleetParticipant((int)Pilot, fleetId, ClientOnly: true)]);
        FakeSdeAccessor sde = new FakeSdeAccessor()
            .Add(77066, "Offertory Sigil", groupId: 4577, categoryId: 11)
            .AddSite(new SdeSite(10347, "Raid: Hall of Sacrifice", 70, "Homefront Operations", null, null, null, null, false, []));
        var toasts = new RecordingToastService();
        using var detector = new HomefrontDetector(gamelog, instance.Services.GetRequiredService<IEventBus>(), sde, toasts,
            new RecordingDialogService(), new UnreadableSettingsServices(instance.Services, logs));

        await _HitAsync(gamelog, "Offertory Sigil", StartedAtUtc);
        await _HitAsync(gamelog, "Offertory Sigil", StartedAtUtc.AddSeconds(5));
        string expected = $"Could not offer a homefront run on {OpsecText.Mark("Raid: Hall of Sacrifice")}.";
        await ActivityWindowHarness.WaitUntil(() => log.Messages.Contains(expected));

        Assert.Contains(expected, log.Messages);
        Assert.Empty(toasts.ActionToasts);
    }

    [AvaloniaFact]
    public async Task SignatureDecisionLog_MarksTheSiteName()
    {
        var log = new RecordingLoggerProvider();
        using var harness = await ActivityWindowHarness.CreateAsync(
            configure: services => services.AddLogging(builder => builder.AddProvider(log)));
        ActivityWindowViewModel model = await _RunOnAnotherSiteAsync(harness);

        await model.ApplySignatureAsync("SUG-270", "Combat Site", "Drone Cluster", []);

        Assert.Single(log.Messages, message =>
            message.StartsWith($"Copied signature {OpsecText.Mark("Drone Cluster")}: the open ", StringComparison.Ordinal));
    }

    private static ActivityOverviewRowViewModel _Row(int solarSystemId, RunRowFacts facts, string? signatureGroup = null) =>
        new(new ActivityOverviewRowDto(Guid.NewGuid(), null, Guid.NewGuid(), ActivityKind.Site, "Angel Hideaway", signatureGroup,
                0, solarSystemId, StartedAtUtc, 600, 1, 1, [], [], 0m, null, 0, false, false, [], IskBreakdown.None,
                IskBreakdown.None, true, []),
            id => $"character {id}", _ => Task.CompletedTask, _ => Task.CompletedTask, facts: facts);

    private static async Task<Guid> _SaveRunAsync(ICqrsDispatcher dispatcher, string siteName, DateTime startedAtUtc,
        string? signature = null)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, 0, siteName, Jita,
            Signature: signature), Ct);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [], [], [], []), Ct);
        return started.Value;
    }

    private static async Task<ActivityWindowViewModel> _RunOnAnotherSiteAsync(ActivityWindowHarness harness)
    {
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureId = "RUS-326";
        model.SignatureName = "Sansha Hideaway";
        await model.StartRunCommand.ExecuteAsync(null);
        return model;
    }

    private static TestClientInstance _KillmailInstance() =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IDialogService>(new RecordingDialogService());
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
                .Add(Gila, "Gila", 26, 6)
                .AddSolarSystem(new SdeSolarSystem(Jita, "Jita", 0.946, "The Forge")));
        });

    /// <summary>A Gila lost on the Angel Hideaway run, while a Serpentis Lookout run of the same pilot was also going —
    /// the run the loss could be moved to.</summary>
    private static async Task<(Guid LinkedRunId, DateTime OtherStartedAtUtc)> _SeedLinkedLossAsync(TestClientInstance instance)
    {
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Ra Vinter", (int)Pilot), Ct);
        Guid linkedRunId = await _SaveRunAsync(dispatcher, "Angel Hideaway", StartedAtUtc);
        DateTime otherStartedAtUtc = StartedAtUtc.AddMinutes(1);
        await _SaveRunAsync(dispatcher, "Serpentis Lookout", otherStartedAtUtc);
        await instance.Services.GetRequiredService<ILocalKillmailRepository>().AddMissingAsync((int)Pilot,
        [
            new LocalKillmail
            {
                CharacterId = (int)Pilot, KillmailId = 1, Hash = "hash1", KillmailTimeUtc = StartedAtUtc.AddMinutes(10),
                SolarSystemId = Jita, IsLoss = true, VictimShipTypeId = Gila, VictimCharacterId = (int)Pilot,
                RunId = linkedRunId, LinkSource = KillmailLinkSource.Auto, ImportedAtUtc = DateTime.UtcNow
            }
        ], Ct);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), Ct);
        return (linkedRunId, otherStartedAtUtc);
    }

    private static async Task<KillmailDetailViewModel> _LoadKillmailAsync(TestClientInstance instance)
    {
        var killmail = new KillmailDetailViewModel(instance.Services.GetRequiredService<ICqrsDispatcher>(),
            instance.Services.GetRequiredService<IDialogService>(), instance.Services, (int)Pilot, 1);
        await killmail.LoadAsync(Ct);
        return killmail;
    }

    private static async Task<ActivityDetailViewModel> _LoadActivityDetailAsync(TestClientInstance instance, Guid runId)
    {
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        IReadOnlyList<ActivityOverviewRowDto> rows = (await dispatcher.Query(new GetActivityOverviewQuery(), Ct)).Value ?? [];
        var detail = new ActivityDetailViewModel(dispatcher, rows.Single(row => row.RunId == runId).ActivitySummaryId,
            sde: instance.Services.GetRequiredService<ISdeAccessor>());
        await detail.LoadAsync(Ct);
        return detail;
    }

    private static async Task _HitAsync(GamelogClientService gamelog, string target, DateTime atUtc)
    {
        var hit = (CombatEvent)(LogLineParser.Parse(
            $"[ {atUtc:yyyy.MM.dd HH:mm:ss} ] (combat) <color=0xff00ffff><b>1192</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>{target}</b><font size=10><color=0x77ffffff> - Nova Rage Heavy Assault Missile - Hits")
            ?? throw new InvalidOperationException("The damage line did not parse."));
        await gamelog.AddHitAsync("Jithran", hit.Direction, hit.Amount, hit.Target, hit.Quality, hit.Timestamp, hit.Weapon);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Answers the server picker by cancelling and keeps its prompt; any other dialog is a failure.</summary>
    public class ServerPromptRecorder : DispatchProxy
    {
        public List<string> Prompts { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IDialogService.SelectServerAsync) || args?[0] is not string prompt)
                throw new NotSupportedException($"{targetMethod?.Name} is not expected while publishing.");

            Prompts.Add(prompt);
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class TwoServers : IClientSessionStore
    {
        public Task SaveAsync(string serverAddress, ClientSessionTokens tokens, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ClientSessionTokens?> LoadAsync(string serverAddress, CancellationToken cancellationToken = default) => Task.FromResult<ClientSessionTokens?>(null);
        public Task<ClientSessionTokens?> LoadForCharacterAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default) => Task.FromResult<ClientSessionTokens?>(null);
        public Task<IReadOnlyList<ClientSessionTokens>> LoadAllAsync(string serverAddress, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClientSessionTokens>>([]);
        public Task SetServerSessionIdAsync(string serverAddress, int characterId, int serverSessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListServersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["et.example:5001", "ops.example:5001"]);
        public Task<IReadOnlyList<string>> ListServersForCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class UnreadableSettingsServices(IServiceProvider inner, ILoggerFactory logs) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISettingRepository) ? new UnreadableSettings()
            : serviceType == typeof(ILoggerFactory) ? logs
            : inner.GetService(serviceType);
    }

    private sealed class UnreadableSettings : ISettingRepository
    {
        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<ClientSetting>>(new InvalidOperationException("database is locked"));

        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeClipboardChangeSource : IClipboardChangeSource
    {
        public string? ClipboardText { get; set; }

        public bool IsSupported => true;

        public event Action? Changed;

        public event Action? SupportChanged
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        public Task<string?> ReadTextAsync() => Task.FromResult(ClipboardText);

        public void RaiseChanged() => Changed?.Invoke();
    }
}
