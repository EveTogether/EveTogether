using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// Whose stored combat the detail screen shows (ET-468): one per screen, so the pilot picked in COMBAT is the one
/// TIMELINE draws and the header counts its idle time on.
/// </summary>
public sealed partial class CombatTelemetryChoice(CqrsDispatcher dispatcher, IReadOnlySet<long>? ownCharacterIds)
    : ObservableObject
{
    private readonly Dictionary<Guid, RunCombatTimelineDto> _timelines = [];
    private readonly Dictionary<Guid, string> _names = [];
    private readonly HashSet<Guid> _storedRuns = [];

    internal const string StoredHitsLabel = "from stored hits";
    public const string LiveOnlyText = "kept only live at the time";

    public ObservableCollection<CombatPilotChipViewModel> Pilots { get; } = [];

    [ObservableProperty] private RunCombatTimelineDto? _shown;
    [ObservableProperty] private string _shownName = string.Empty;
    [ObservableProperty] private bool _fromStoredHits;
    [ObservableProperty] private string? _idleText;

    public bool HasAny => _timelines.Count > 0;

    public async Task LoadAsync(RunDetailSectionInput input, CancellationToken cancellationToken)
    {
        _timelines.Clear();
        _storedRuns.Clear();
        _names.Clear();
        foreach (ActivityRunDetailDto run in input.Detail.Runs)
        {
            _names[run.RunId] = input.NameOf(run.CharacterId);
            Result<RunCombatTimelineDto?> read = await dispatcher.Query(new GetRunCombatTimelineQuery(run.RunId), cancellationToken);
            if (read.IsSuccess && read.Value is { } timeline)
            {
                _timelines[run.RunId] = timeline;
                continue;
            }

            // No stored timeline (saved before ET-467): the pilot's stored hits still give damage.
            read = await dispatcher.Query(new GetRunStoredHitsCombatQuery(run.RunId), cancellationToken);
            if (read.IsSuccess && read.Value is { } derived)
            {
                _timelines[run.RunId] = derived;
                _storedRuns.Add(run.RunId);
            }
        }

        Pilots.Clear();
        foreach (ActivityRunDetailDto run in input.Detail.Runs)
        {
            bool isAvailable = _timelines.ContainsKey(run.RunId);
            // A fleet mate's run carries its combat only when that pilot shares it (ET-472).
            string why = ownCharacterIds is { } own && !own.Contains(run.CharacterId) ? "not shared" : "not recorded";
            Pilots.Add(new CombatPilotChipViewModel(run.RunId,
                isAvailable ? _names[run.RunId] : $"{_names[run.RunId]} · {why}", isAvailable, _Show));
        }

        Guid? first = Pilots.FirstOrDefault(pilot => pilot.IsAvailable)?.RunId;
        if (first is { } runId)
        {
            _Show(runId);
            return;
        }

        Shown = null;
        ShownName = string.Empty;
        IdleText = null;
    }

    private void _Show(Guid runId)
    {
        foreach (CombatPilotChipViewModel pilot in Pilots)
        {
            pilot.IsSelected = pilot.RunId == runId;
        }

        ShownName = _names[runId];
        FromStoredHits = _storedRuns.Contains(runId);
        Shown = _timelines[runId];
        int idle = CombatChartModel.IdleSeconds(Shown).Count(second => second);
        IdleText = $"idle {TimeSpan.FromSeconds(idle):mm\\:ss}";
    }
}
