using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-323: the AUTO-JOIN chip beside a fleet's name carries a per-fleet override of auto-join, three-valued
/// because "off" and "never set" are different facts — DEFAULT tracks whatever the central setting says, so it has
/// to be told apart from a member who pinned this one fleet to NEVER while everything else auto-joins.
/// </summary>
public class FleetAutoJoinChipTests
{
    private const string Server = "srv:7443";
    private const int Me = 100;

    private static FleetInfo Fleet(long id, string name, int owner) =>
        new(id, name, null, FleetVisibility.Public, FleetState.Active, owner, null, null,
            DateTimeOffset.UnixEpoch, FleetActivation.Forming, null);

    private static FleetMemberInfo Member(long memberId, int characterId, FleetRole role) =>
        new(memberId, characterId, 0, 0, role, false, null, null);

    /// <summary>
    /// Waits past <c>ServerGroups</c> filling in — the row exists there before its chip is set, since
    /// <c>ReloadAsync</c> rebuilds the rows before it computes their AUTO-JOIN state — so this polls the chip
    /// label itself rather than just the row's presence.
    /// </summary>
    private static async Task<FleetsViewModel> LoadedVmAsync(TestClientInstance instance)
    {
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        await sessions.SaveAsync(Server, new ClientSessionTokens("t", "r", "RaymondKrah", Me));
        var vm = new FleetsViewModel(instance.Services);
        for (var i = 0; i < 100 && !_ChipIsLoaded(vm); i++)
        {
            await Task.Delay(50);
        }

        return vm;
    }

    private static bool _ChipIsLoaded(FleetsViewModel vm) =>
        vm.ServerGroups.Count > 0
        && vm.ServerGroups[0].Fleets.Count > 0
        && vm.ServerGroups[0].Fleets[0].AutoJoinChipLabel.Length > 0;

    /// <summary>
    /// Seeds the starting override: null leaves the key absent (DEFAULT), anything else writes it.
    /// </summary>
    private static Task _SeedOverrideAsync(IDispatcher dispatcher, string key, string? value) =>
        value is null ? Task.CompletedTask : dispatcher.Send(new SetSettingCommand(key, value));

    [AvaloniaTheory]
    [InlineData(null, "true")]     // DEFAULT -> ALWAYS
    [InlineData("true", "false")]  // ALWAYS -> NEVER
    [InlineData("false", null)]    // NEVER -> DEFAULT, which deletes the key rather than leaving an explicit value
    public async Task AutoJoinChip_Clicked_AdvancesDefaultAlwaysNever(string? startingValue, string? expectedValueAfterClick)
    {
        var transport = new RecordingFleetTransportClient();
        transport.MyFleetsByServer[Server] = [Fleet(11, "Op", Me)];
        transport.MembersByFleet[11] = [Member(1, Me, FleetRole.FleetCommander)];

        using var instance = TestClientInstance.Create(s =>
        {
            s.AddSingleton<IFleetTransportClient>(transport);
            s.AddSingleton<IDialogService>(new RecordingDialogService());
        });
        var dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        string key = FleetRunWindowPresenter.PerFleetAutoOpenSettingKey(11);
        await _SeedOverrideAsync(dispatcher, key, startingValue);

        var vm = await LoadedVmAsync(instance);
        var row = Assert.Single(Assert.Single(vm.ServerGroups).Fleets);
        Assert.True(row.ShowAutoJoinChip);

        await vm.CycleAutoJoinCommand.ExecuteAsync(row);

        var settings = await dispatcher.Query(new GetSettingsQuery());
        Assert.Equal(expectedValueAfterClick, settings.FirstOrDefault(s => s.Key == key)?.Value);
    }
}
