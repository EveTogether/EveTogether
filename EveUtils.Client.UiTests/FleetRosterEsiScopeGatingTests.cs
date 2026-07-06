using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ESI fleet actions are gated on the acting/boss character holding the required scope: the scope-requiring
/// buttons disable and explain through a tooltip when the scope is missing, and an attempt also raises an "ESI access
/// required" toast (the status bar is too easy to miss). COUPLE needs read_fleet on the acting
/// character; the MOTD/structure/invite control row needs write_fleet on the boss.
/// </summary>
public class FleetRosterEsiScopeGatingTests
{
    private const int Boss = 95000030;
    private const string AccessRequired = "ESI access required";

    [AvaloniaFact]
    public async Task Couple_WhenReadScopeMissing_IsDisabledWithTooltip()
    {
        using var instance = TestClientInstance.Create();
        var (info, client) = await SeedFleetAsync(instance.Services, coupled: false, grantScope: false);
        using var roster = new FleetRosterViewModel(instance.Services, client, info, isOwner: true, Boss);
        await roster.RefreshCommand.ExecuteAsync(null);

        Assert.False(roster.EsiReadAllowed);   // COUPLE is disabled
        Assert.NotNull(roster.EsiReadTooltip); // the tooltip names the missing scope
    }

    [AvaloniaFact]
    public async Task Couple_WhenReadScopeGranted_IsEnabled()
    {
        using var instance = TestClientInstance.Create();
        var (info, client) = await SeedFleetAsync(instance.Services, coupled: false, grantScope: true);
        using var roster = new FleetRosterViewModel(instance.Services, client, info, isOwner: true, Boss);
        await roster.RefreshCommand.ExecuteAsync(null);

        Assert.True(roster.EsiReadAllowed);
        Assert.Null(roster.EsiReadTooltip);
    }

    [AvaloniaFact]
    public async Task ControlRow_WhenWriteScopeMissing_IsDisabledWithTooltip()
    {
        using var instance = TestClientInstance.Create();
        var (info, client) = await SeedFleetAsync(instance.Services, coupled: true, grantScope: false);
        using var roster = new FleetRosterViewModel(instance.Services, client, info, isOwner: true, Boss);
        await roster.RefreshCommand.ExecuteAsync(null);

        Assert.False(roster.EsiWriteAllowed);
        Assert.NotNull(roster.EsiWriteTooltip);
    }

    [AvaloniaFact]
    public async Task Couple_WhenInvokedWithoutReadScope_RaisesToast_AndDoesNotCouple()
    {
        var toasts = new RecordingToastService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IToastService>(toasts));
        var (info, client) = await SeedFleetAsync(instance.Services, coupled: false, grantScope: false);
        using var roster = new FleetRosterViewModel(instance.Services, client, info, isOwner: true, Boss);

        await roster.CoupleEsiFleetCommand.ExecuteAsync(null);

        Assert.Contains(toasts.Toasts, toast => toast.Title == AccessRequired && toast.Kind == ToastKind.Error);
        Assert.False(roster.HasEsiFleet); // the missing read scope stops COUPLE before any detection
    }

    private static async Task<(FleetInfo Info, LocalFleetClient Client)> SeedFleetAsync(IServiceProvider services, bool coupled, bool grantScope)
    {
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        string[] scopes = grantScope ? [FleetsScopeCatalog.ReadFleet, FleetsScopeCatalog.WriteFleet] : [];
        await characters.AddOrUpdateAsync(new Character("Boss", Boss, GrantedScopes: scopes));

        var created = await fleetService.CreateLocalFleetAsync("gating test", null, Boss);
        var fleet = (await repository.GetAsync(created.Value, TestContext.Current.CancellationToken))!;
        var info = new FleetInfo(fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
            fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, FleetActivation.Forming,
            EsiFleetId: coupled ? 999 : null, EsiFleetBossId: coupled ? Boss : null);
        return (info, new LocalFleetClient(fleetService, repository, characters, Boss));
    }
}
