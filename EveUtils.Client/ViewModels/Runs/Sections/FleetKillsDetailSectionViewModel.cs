using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Formatting;
using EveUtils.Client.Killmails;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Queries;
using EveUtils.Shared.Modules.Runs.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>One kill on FLEET KILLS (ET-373) and its way into the killmail detail (ET-333).</summary>
public sealed partial class FleetKillViewModel(Action? open)
{
    public required string ShipText { get; init; }

    public required string VictimText { get; init; }

    public required string MembersText { get; init; }

    public required string FinalBlowText { get; init; }

    public required string TimeText { get; init; }

    public required string ValueText { get; init; }

    public bool CanOpen => open is not null;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => open?.Invoke();
}

/// <summary>
/// FLEET KILLS on the detail screen of a group run (ET-373): what the fleet destroyed while the group ran, one line per
/// killmail from the local rows ET-371 keeps. Its destroyed sum is information only — a kill is neither a cost nor an
/// income of the run, so the section has no <c>IskSource</c> and TOTAL ISK never reads it.
/// </summary>
public sealed class FleetKillsDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.FleetKills, "FLEET KILLS")
{
    public ObservableCollection<FleetKillViewModel> Kills { get; } = [];

    // Known only after LoadAsync, so the screen places this section again once every section has loaded.
    public override bool HasContent => Kills.Count > 0;

    public override void Apply(RunDetailSectionInput input) => HeaderSummary = "none";

    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        ActivityDetailDto detail = input.Detail;
        IReadOnlyList<RunFleetKillDto> kills = [];
        if (detail.GroupCode is not null && detail.Runs.Count > 0)
        {
            // A group run still open has no end yet: the period then runs to now.
            DateTime? stoppedUtc = detail.Runs.Any(run => run.StoppedAtUtc is null) ? null : detail.Runs.Max(run => run.StoppedAtUtc);
            Result<IReadOnlyList<RunFleetKillDto>> read = await services.Dispatcher.Query(new GetRunFleetKillsQuery(
                [.. detail.Runs.Select(run => (int)run.CharacterId)], detail.Runs.Min(run => run.StartedAtUtc), stoppedUtc), cancellationToken);
            kills = read.IsSuccess && read.Value is { } value ? value : [];
        }

        KillmailNames? names = DetailKillmailNames.Build(services);
        if (names is not null)
        {
            await names.HydrateAsync(
                [.. kills.SelectMany(kill => new[] { kill.VictimCharacterId, kill.FinalBlow?.CharacterId }).OfType<int>()],
                [.. kills.SelectMany(kill => new[] { kill.VictimCorporationId, kill.FinalBlow?.CorporationId }).OfType<int>()],
                [],
                cancellationToken);
        }

        Kills.Clear();
        foreach (RunFleetKillDto kill in kills)
        {
            int characterId = kill.OpenCharacterId;
            int killmailId = kill.KillmailId;
            string ship = KillmailRunLinker.IsCapsule(kill.VictimShipTypeId) ? "pod" : DetailKillmailNames.TypeName(services.Sde, kill.VictimShipTypeId);
            Kills.Add(new FleetKillViewModel(services.Services is not null ? () => _OpenKillmail(characterId, killmailId) : null)
            {
                ShipText = kill.HasPod ? $"{ship} + pod" : ship,
                VictimText = kill.VictimCharacterId is { } victim ? names?.NameOf(victim) ?? $"character {victim}" : "unknown pilot",
                MembersText = string.Join(", ", kill.MemberCharacterIds.Select(member => input.NameOf(member))),
                FinalBlowText = kill.FinalBlow is { } finalBlow ? DetailKillmailNames.FinalBlowText(finalBlow, names, services.Sde) : "unknown",
                TimeText = $"{kill.KillmailTimeUtc.ToLocalTime():d MMM HH:mm:ss}",
                ValueText = kill.DestroyedValue is { } value ? IskFormat.Compact(value) + " ISK" : "no price"
            });
        }

        decimal? destroyed = kills.Any(kill => kill.DestroyedValue is not null) ? kills.Sum(kill => kill.DestroyedValue.GetValueOrDefault()) : null;
        HeaderSummary = Kills.Count == 0 ? "none" : $"destroyed: {(destroyed is { } sum ? IskFormat.Compact(sum) + " ISK" : "no price")}";
    }

    private void _OpenKillmail(int characterId, int killmailId)
    {
        if (services.Services is not { } provider || provider.GetService<IDialogService>() is not { } dialogs)
        {
            return;
        }

        dialogs.ShowKillmailDetail(new KillmailDetailViewModel(services.Dispatcher, dialogs, provider, characterId, killmailId));
    }
}
