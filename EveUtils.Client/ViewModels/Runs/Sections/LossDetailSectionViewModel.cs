using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.Killmails;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Queries;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;

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

    // Built once, from whatever RunDetailSectionServices.Services can resolve — a section built without an
    // IServiceProvider (or the tests that pass every dependency as null) falls back to the SDE-only names below.
    private KillmailNames? _names;
    private bool _namesBuilt;

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

        if (!_namesBuilt)
        {
            _names = _BuildNames();
            _namesBuilt = true;
        }

        if (_names is { } names)
        {
            await names.HydrateAsync(
                [.. losses.Select(loss => loss.FinalBlow?.CharacterId).OfType<int>()],
                [.. losses.Select(loss => loss.FinalBlow?.CorporationId).OfType<int>()],
                [],
                cancellationToken);
        }

        Losses.Clear();
        foreach (RunLossDto loss in losses)
        {
            ActivityRunDetailDto? run = detail.Runs.FirstOrDefault(candidate => candidate.RunId == loss.RunId);
            int lossCharacterId = loss.CharacterId;
            int lossKillmailId = loss.KillmailId;
            Losses.Add(new LinkedLossViewModel(services.Dispatcher, loss.CharacterId, loss.KillmailId,
                [.. loss.OtherRuns.Select(other => new LinkedLossRunChoice(other.RunId,
                    $"{other.SiteName ?? "unnamed run"} · {other.StartedAtUtc.ToLocalTime():d MMM HH:mm}"))],
                _ChangedAsync,
                services.Services is not null ? () => _OpenKillmail(lossCharacterId, lossKillmailId) : null)
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

    // OPEN KILLMAIL (ET-333): the same "no service, no action" rule every other lookup in this section follows —
    // a section built without an IServiceProvider never offers the button at all (LinkedLossViewModel.CanOpenKillmail).
    private void _OpenKillmail(int characterId, int killmailId)
    {
        if (services.Services is not { } provider || provider.GetService<IDialogService>() is not { } dialogs)
        {
            return;
        }

        dialogs.ShowKillmailDetail(new KillmailDetailViewModel(services.Dispatcher, dialogs, provider, characterId, killmailId));
    }

    private static string _Reason(RunLossDto loss) => loss.LinkSource switch
    {
        KillmailLinkSource.Manual => "linked by hand",
        _ when KillmailRunLinker.IsCapsule(loss.VictimShipTypeId) => "the pod followed its ship within a minute",
        _ => "the only run of this pilot at that time, place and hull"
    };

    // The one place a final blow is named: a player through KillmailNames (ET-336, hydrated above), an NPC
    // corporation or faction from the SDE — the same split KillmailsOverviewViewModel draws (ET-332).
    private string _FinalBlowText(KillmailFinalBlowDto finalBlow)
    {
        string who = finalBlow switch
        {
            { CharacterId: { } character } => _names?.NameOf(character) ?? $"character {character}",
            { CorporationId: { } corporation } =>
                _names?.NameOf(corporation) ?? services.Sde?.GetNpcCorporationName(corporation) ?? $"corporation {corporation}",
            { FactionId: { } faction } => services.Sde?.GetFactionName(faction) ?? $"faction {faction}",
            _ => "unknown"
        };
        return finalBlow.ShipTypeId is { } ship ? $"{who} in {_TypeName(ship)}" : who;
    }

    private string _TypeName(int typeId) => services.Sde?.GetType(typeId)?.Name ?? $"type {typeId}";

    // Every dependency here is optional, like the rest of this section (RunDetailSectionServices' own rule): missing
    // any one of them means no live player-name resolution, not a crash — _FinalBlowText falls back to the bare id.
    private KillmailNames? _BuildNames()
    {
        if (services.Services is not { } provider || services.Sde is not { } sde)
        {
            return null;
        }

        IEsiAffiliationResolver? affiliation = provider.GetService<IEsiAffiliationResolver>();
        IKillmailEntityNameRepository? repository = provider.GetService<IKillmailEntityNameRepository>();
        ISettingRepository? settings = provider.GetService<ISettingRepository>();
        if (affiliation is null || repository is null || settings is null)
        {
            return null;
        }

        Dictionary<int, string> ownNames = (services.OwnCharacterIds ?? new HashSet<long>())
            .ToDictionary(id => (int)id, id => services.NameOf?.Invoke(id) ?? id.ToString());
        return new KillmailNames(ownNames, provider.GetService<IExternalCharacterLookup>(), affiliation, sde, repository,
            settings, provider.GetService<TimeProvider>() ?? TimeProvider.System);
    }
}
