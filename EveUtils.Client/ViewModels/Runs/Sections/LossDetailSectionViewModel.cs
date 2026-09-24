using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Queries;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>LINKED LOSS on the detail screen (ET-331): each own loss linked to the activity's runs — hull, fit, final
/// blow and why it was linked — with the pilot's way to move or unlink it. The cost is the registry's own share of TOTAL
/// ISK, never a total of its own.</summary>
public sealed partial class LossDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Loss, "LINKED LOSS")
{
    public ObservableCollection<LinkedLossViewModel> Losses { get; } = [];

    [ObservableProperty] private string? _emptyText;

    private bool _hasLoss;

    // From the stored breakdown rather than the losses read below: the screen places its sections before that read,
    // and a linked loss always leaves a SHIP LOSS share, priced or not.
    public override bool HasContent => _hasLoss;

    public override void Apply(RunDetailSectionInput input)
    {
        IskContribution? share = input.Detail.Isk.Of(IskSource.ShipLoss);
        _hasLoss = share is not null;
        HeaderSummary = share is null
            ? "none linked"
            : share.Certainty is IskCertainty.Unknown ? "not priced yet" : IskFormat.Whole(share.Amount);
    }

    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        ActivityDetailDto detail = input.Detail;
        Result<IReadOnlyList<RunLossDto>> read = await services.Dispatcher.Query(
            new GetRunLossesQuery([.. detail.Runs.Select(run => run.RunId)]), cancellationToken);
        IReadOnlyList<RunLossDto> losses = read.IsSuccess && read.Value is { } value ? value : [];

        Losses.Clear();
        foreach (RunLossDto loss in losses)
        {
            ActivityRunDetailDto? run = detail.Runs.FirstOrDefault(candidate => candidate.RunId == loss.RunId);
            Losses.Add(new LinkedLossViewModel(services.Dispatcher, loss.CharacterId, loss.KillmailId,
                [.. loss.OtherRuns.Select(other => new LinkedLossRunChoice(other.RunId,
                    $"{other.SiteName ?? "unnamed run"} · {other.StartedAtUtc.ToLocalTime():d MMM HH:mm}"))],
                _ChangedAsync)
            {
                ShipText = _TypeName(loss.VictimShipTypeId),
                FitText = run?.FitNameSnapshot ?? "no fit recorded",
                TimeText = $"{loss.KillmailTimeUtc.ToLocalTime():d MMM HH:mm:ss}",
                FinalBlowText = loss.FinalBlow is { } finalBlow ? _FinalBlowText(finalBlow) : "unknown",
                ReasonText = _Reason(loss)
            });
        }

        EmptyText = Losses.Count == 0 ? "No loss is linked to this activity." : null;
    }

    private Task _ChangedAsync()
    {
        RaiseActivityCorrected();
        return Task.CompletedTask;
    }

    private static string _Reason(RunLossDto loss) => loss.LinkSource switch
    {
        KillmailLinkSource.Manual => "linked by hand",
        _ when KillmailRunLinker.IsCapsule(loss.VictimShipTypeId) => "the pod followed its ship within a minute",
        _ => "the only run of this pilot at that time, place and hull"
    };

    // The one place a final blow is named. ponytail: a player shows as an id until ET-336's KillmailNames is wired in
    // here with one HydrateAsync per read; NPC corporations and factions already come from the SDE.
    private string _FinalBlowText(KillmailFinalBlowDto finalBlow)
    {
        string who = finalBlow switch
        {
            { CharacterId: { } character } => $"character {character}",
            { CorporationId: { } corporation } => services.Sde?.GetNpcCorporationName(corporation) ?? $"corporation {corporation}",
            { FactionId: { } faction } => services.Sde?.GetFactionName(faction) ?? $"faction {faction}",
            _ => "unknown"
        };
        return finalBlow.ShipTypeId is { } ship ? $"{who} in {_TypeName(ship)}" : who;
    }

    private string _TypeName(int typeId) => services.Sde?.GetType(typeId)?.Name ?? $"type {typeId}";
}
