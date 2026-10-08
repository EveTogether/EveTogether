using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>LOOT on the detail screen: grouped by character and correctable after the fact (ET-215) — the same
/// component the run window's LOOT section is, so the two read the same.</summary>
public sealed partial class LootDetailSectionViewModel : RunDetailSection
{
    private readonly RunDetailSectionServices _services;
    private bool _hasCaptures;

    public LootDetailSectionViewModel(RunDetailSectionServices services) : base(RunSectionId.Loot, "LOOT")
    {
        _services = services;
        LootOverview = new ActivityLootViewModel(
            // ET-364: the selector (when the app wired one through Services) picks up the user's chosen provider
            // fresh each price refresh; services.Appraisal only still matters for callers that never set Services.
            () => new RunLootViewModel(services.Dispatcher, services.Appraisal, services.Sde, services.Images,
                services.Services?.GetService<IAppraisalProviderSelector>()),
            services.Portraits);
        LootOverview.LootCorrected += RaiseActivityCorrected;
    }

    public ActivityLootViewModel LootOverview { get; }

    [ObservableProperty] private string? _lootEmptyText;

    /// <summary>B6 (ET-466): why an abyssal run says nothing about its containers, in place of a count nobody has.</summary>
    [ObservableProperty] private string? _containersText;

    public override bool HasContent => _hasCaptures;

    /// <summary>What the summary says about the loot. The figure in the header is this section's share of TOTAL ISK as
    /// the registry counted it (ET-256) — the very number the runs overview and TOTAL ISK are made of.</summary>
    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        ContainersText = input.RunType.Space is RunSpace.AbyssalPocket
            ? "Containers opened: not counted. Opening or looting a container writes no line to the game log."
            : null;
        RunLootCaptureDto[] captures = [.. detail.Runs
            .SelectMany(run => run.LootCaptures)
            .Where(capture => capture.Role is not LootCaptureRole.Consumed)];
        _hasCaptures = captures.Length > 0;
        LootEmptyText = captures.Length > 0
            ? null
            : "No loot capture was recorded for this activity — nothing was copied, so there is nothing to value.";
        // "no price" and not "0 ISK": a figure nobody has must not look like a figure that came out at zero (ET-65 AC-5).
        decimal? net = detail.Isk.Of(IskSource.Loot) is { Certainty: not IskCertainty.Unknown } loot ? loot.Amount : null;
        // The filament is the CONSUMABLES section's own share (ET-329), handed to the loot totals so CONSUMED and NET
        // say what the run really cost; the header above stays the loot's own share, so it is never taken off twice.
        LootOverview.SetFilament(detail.Isk.Of(IskSource.Consumables) is { Certainty: not IskCertainty.Unknown } consumables
            ? -consumables.Amount
            : null);
        HeaderSummary = captures.Length > 0
            ? $"{IskFormat.WholeOrNoPrice(net)} · {captures.Length} captures · {captures.Count(capture => capture.IsExcluded)} excluded"
            : "nothing captured";
    }

    /// <summary>
    /// One block per run, each showing that run's own captures (ET-215) — the grouping ET-211 made true, since a
    /// capture now lands on the run of the character who copied it. A run from before that carries the whole
    /// group's loot and the others carry none, and that is exactly how it is shown: no share is worked out after
    /// the fact that was never recorded. Largest first on the way in, like BOUNTY and ENEMIES; a later re-read keeps
    /// the order, so a correction never moves the block the pilot is working in.
    /// </summary>
    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        bool isFirstRead = LootOverview.Characters.Count == 0;
        foreach (ActivityRunDetailDto run in input.Detail.Runs)
        {
            ActivityLootCharacterViewModel block = LootOverview.Show(run.RunId, run.CharacterId,
                input.NameOf(run.CharacterId));
            block.Loot.IsLocked = true;
            block.Loot.IsReadOnly = _services.OwnCharacterIds is { } own && !own.Contains(run.CharacterId);
            block.Loot.SpentFilament = _SpentFilament(input.Detail, run.RunId);
            block.Loot.SetRooms(RunRooms.Boundaries(input.Detail.Parameters, run.RunId), run.StartedAtUtc, run.StoppedAtUtc,
                isNewestFirst: false);
            await block.Loot.LoadWhenIdleAsync(run.LootCaptures, cancellationToken);
        }

        LootOverview.Keep([.. input.Detail.Runs.Select(run => run.RunId)]);
        if (isFirstRead)
            LootOverview.OrderByValue();
    }

    /// <summary>The filament the run was saved against and its count, which CONSUMABLES counts (ET-483).</summary>
    private static (int TypeId, int Count)? _SpentFilament(ActivityDetailDto detail, Guid runId) =>
        _ParsedInt(detail, runId, RunParameterKey.AbyssalFilamentCount) is > 0 and var count
        && _ParsedInt(detail, runId, RunParameterKey.AbyssalFilamentTypeId) is { } typeId
            ? (typeId, count)
            : null;

    private static int? _ParsedInt(ActivityDetailDto detail, Guid runId, RunParameterKey key) =>
        detail.Parameters.FirstOrDefault(parameter => parameter.RunId == runId && parameter.ParameterKey == key) is { } stored
        && int.TryParse(stored.TypedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;

    public override string AbsentReason(string noun) => $"no LOOT — {noun} leaves no wrecks to empty";
}
