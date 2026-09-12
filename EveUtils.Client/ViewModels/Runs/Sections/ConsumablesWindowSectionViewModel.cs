using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Esi;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// CONSUMABLES in the run window: the abyssal filament CONSUMED, one row per character in the group (ET-249). The
/// filament type comes from the pocket's own stored tier and weather, valued through the price source; a
/// character's own count is a proposal from their fit's hull class, left for the pilot to change and never
/// re-derived once the row exists. Contributes a negative share of TOTAL ISK — see <see cref="IskSource.Consumables"/>.
/// </summary>
public sealed partial class ConsumablesWindowSectionViewModel(IRunWindowContext context)
    : RunWindowSection(context, RunSectionId.Consumables, "CONSUMABLES")
{
    private int? _filamentTypeId;
    private int? _pricedForTypeId;

    public ObservableCollection<ConsumableRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string? _filamentName;

    [ObservableProperty] private decimal? _unitPrice;

    public override void Refresh(DateTime nowUtc)
    {
        _SyncRows();
        _ = _RefreshFilamentAsync();
    }

    public override void RefreshSummary()
    {
        foreach (ConsumableRowViewModel row in Rows)
            row.Reprice(UnitPrice);

        int totalCount = Rows.Sum(row => row.Count ?? 0);
        string filament = FilamentName ?? "filament";
        HeaderSummary = totalCount == 0
            ? "no filament count set"
            : _TotalCost() is { } cost
                ? $"-{IskFormat.Whole(cost)} — {totalCount}x {filament}"
                : $"{totalCount}x {filament} — no price yet";
    }

    /// <summary>What SAVE stores for one run of the group: this character's own confirmed count, and the shared
    /// filament type it was priced against — never written for a run whose count was never confirmed (ET-249).</summary>
    public override void AddToSave(RunSaveDraft draft)
    {
        if (Rows.FirstOrDefault(row => row.RunId == draft.RunId)?.Count is not { } count)
            return;

        draft.Parameters.Add(new RunParameterInput
        {
            ParameterKey = RunParameterKey.AbyssalFilamentCount,
            TypedValue = count.ToString(CultureInfo.InvariantCulture),
            ObservedAtUtc = DateTime.UtcNow
        });
        if (_filamentTypeId is { } typeId)
            draft.Parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.AbyssalFilamentTypeId,
                TypedValue = typeId.ToString(CultureInfo.InvariantCulture),
                ObservedAtUtc = DateTime.UtcNow
            });
    }

    private decimal? _TotalCost() =>
        UnitPrice is { } price && Rows.Any(row => row.Count is not null)
            ? Rows.Sum(row => (row.Count ?? 0) * price)
            : null;

    /// <summary>One row per participant (a solo run is its own one-participant group, ET-131's own definition) —
    /// added once and never rebuilt, so an edit already made survives the next clock tick.</summary>
    private void _SyncRows()
    {
        List<RunParticipantViewModel> wanted = [.. Context.Participants];
        foreach (ConsumableRowViewModel gone in Rows
                     .Where(row => wanted.All(participant => participant.RunId != row.RunId)).ToList())
            Rows.Remove(gone);

        foreach (RunParticipantViewModel participant in wanted)
        {
            if (Rows.Any(row => row.RunId == participant.RunId))
                continue;

            var row = new ConsumableRowViewModel(
                participant.RunId, participant.CharacterId, participant.CharacterName, _ProposedCount(participant.CharacterId));
            row.PropertyChanged += _OnRowChanged;
            Rows.Add(row);
        }

        RefreshSummary();
    }

    private void _OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsumableRowViewModel.Count))
            RefreshSummary();
    }

    /// <summary>A destroyer needs 2 (Jithran, ET-249) — every other hull class proposes nothing until someone
    /// measures the SDE's own filament-access requirement for it, per the ticket's own instruction never to guess.</summary>
    private int? _ProposedCount(int characterId)
    {
        if (Context.Services.GetService<IShipFitDetectionService>() is not { } detection
            || Context.Services.GetService<ISdeAccessor>() is not { } sde)
            return null;

        int? shipTypeId = detection.GetReading(characterId).ShipTypeId;
        string? hullClass = shipTypeId is { } typeId && sde.IsAvailable && sde.GetType(typeId) is { } type
            ? sde.GetGroup(type.GroupId)?.Name
            : null;
        return AbyssalConsumables.ProposedCount(hullClass);
    }

    /// <summary>The filament CONSUMABLES prices against: resolved from the pocket's own tier and weather the moment
    /// both are set, then priced once per resolved type — a tier/weather change (mid-run, ET-241) re-prices, a tick
    /// that resolves to the same type does not ask the price source again.</summary>
    private async Task _RefreshFilamentAsync()
    {
        if (Context.Services.GetService<ISdeAccessor>() is not { } sde
            || Context.TierIndex is not { } tier || Context.Weather is not { } weather)
        {
            _filamentTypeId = null;
            FilamentName = null;
            RefreshSummary();
            return;
        }

        int? typeId = AbyssalConsumables.ResolveTypeId(sde, tier, weather.Name);
        _filamentTypeId = typeId;
        FilamentName = typeId is not null ? $"{AbyssalTiers.Names[tier]} {weather.Name} Filament" : null;
        RefreshSummary();

        if (typeId is null || typeId == _pricedForTypeId
            || Context.Services.GetService<IAppraisalProvider>() is not { } appraisal)
            return;

        _pricedForTypeId = typeId;
        Result<AppraisalOutcome> valued = await appraisal.AppraiseAsync([new AppraisalLine(typeId.Value, string.Empty, 1)]);
        UnitPrice = valued.IsSuccess
            ? valued.Value!.Rows.FirstOrDefault(row => row.Line.TypeId == typeId)?.Price?.Estimate is { } estimate
                ? (decimal)estimate
                : null
            : null;
        RefreshSummary();
    }

    protected override void OnContextChanged(string? propertyName)
    {
        if (propertyName is nameof(IRunWindowContext.TierIndex) or nameof(IRunWindowContext.Weather))
            _pricedForTypeId = null;
    }
}
