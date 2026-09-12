using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

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
            () => new RunLootViewModel(services.Dispatcher, services.Appraisal, services.Sde, services.Images),
            services.Portraits);
        LootOverview.LootCorrected += RaiseActivityCorrected;
    }

    public ActivityLootViewModel LootOverview { get; }

    [ObservableProperty] private string? _lootEmptyText;

    public override bool HasContent => _hasCaptures;

    /// <summary>What the summary says about the loot. The figure in the header is this section's share of TOTAL ISK as
    /// the registry counted it (ET-256) — the very number the runs overview and TOTAL ISK are made of.</summary>
    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        RunLootCaptureDto[] captures = [.. detail.Runs.SelectMany(run => run.LootCaptures)];
        _hasCaptures = captures.Length > 0;
        LootEmptyText = captures.Length > 0
            ? null
            : "No loot capture was recorded for this activity — nothing was copied, so there is nothing to value.";
        // "no price" and not "0 ISK": a figure nobody has must not look like a figure that came out at zero (ET-65 AC-5).
        decimal? net = detail.Isk.Of(IskSource.Loot) is { Certainty: not IskCertainty.Unknown } loot ? loot.Amount : null;
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
            await block.Loot.LoadWhenIdleAsync(run.LootCaptures, cancellationToken);
        }

        LootOverview.Keep([.. input.Detail.Runs.Select(run => run.RunId)]);
        if (isFirstRead)
            LootOverview.OrderByValue();
    }

    public override string AbsentReason(string noun) => $"no LOOT — {noun} leaves no wrecks to empty";
}
