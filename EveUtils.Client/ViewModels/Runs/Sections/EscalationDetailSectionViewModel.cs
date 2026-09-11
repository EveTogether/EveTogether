using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>ESCALATION on the detail screen: the site this run led to, where, and how far it is from the pilot now.</summary>
public sealed partial class EscalationDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Escalation, "ESCALATION")
{
    // The destination the jump count on screen was read for — only a different one is worth asking ESI again for.
    private string? _jumpsDestination;

    [ObservableProperty] private string? _escalationText;
    [ObservableProperty] private string? _escalationObservedText;
    [ObservableProperty] private string? _escalationSystemText;
    [ObservableProperty] private string? _escalationExpiresAtText;
    [ObservableProperty] private string? _escalationJumpsText;
    [ObservableProperty] private string? _escalationJumpsEmptyText;
    [ObservableProperty] private string? _escalationEmptyText;

    public override bool HasContent => EscalationText is not null;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        RunParameterDto? escalation = detail.Parameters
            .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.Escalation);
        EscalationText = escalation?.TypedValue;
        EscalationObservedText = escalation is null
            ? null
            : $"read from the Agency at {escalation.ObservedAtUtc.ToLocalTime():HH:mm} on " +
              $"{escalation.ObservedAtUtc.ToLocalTime():d MMM}";
        EscalationEmptyText = escalation is null ? "No escalation has been registered for this activity." : null;
        HeaderSummary = escalation?.TypedValue ?? "none registered";

        EscalationSystemText = detail.Parameters
            .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationSystem)?.TypedValue;
        EscalationExpiresAtText = detail.Parameters
                .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationExpiresAtUtc)?.TypedValue
            is { } expiresAt && DateTime.TryParse(expiresAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime expiresAtUtc)
            ? $"expires {expiresAtUtc.ToLocalTime():HH:mm} on {expiresAtUtc.ToLocalTime():d MMM}"
            : null;
    }

    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        string? destination = _DestinationOf(input.Detail);
        // The jump count is a live ESI read (ET-127); a follow-up only asks it again for a changed destination.
        if (followUp && destination == _jumpsDestination)
            return;

        (EscalationJumpsText, EscalationJumpsEmptyText) = await _JumpsAsync(input.Detail, cancellationToken);
        _jumpsDestination = destination;
    }

    public override string AbsentReason(string noun) => $"no ESCALATION — {noun} does not escalate";

    private static string? _DestinationOf(ActivityDetailDto detail) =>
        detail.Parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationSolarSystemId)
            ?.TypedValue;

    /// <summary>
    /// The jump count to the escalation's destination, read fresh at display time and never stored (ET-127) — an
    /// escalation typed offline stays fully usable; only this one line needs ESI, through the existing ESI client and
    /// its own disk-level cache. <see cref="EscalationJumpsEmptyText"/> carries why whenever the count itself is null,
    /// the same "empty is a state, not silence" rule as every other empty text on this screen — a hidden JUMPS row
    /// would read as "this escalation has no destination", which is a different fact from "the count could not be
    /// read". The pair is (null, null) only when there is no destination to count a distance to at all.
    ///
    /// Origin is the character's current location rather than the run's own system: ET-124 never established which
    /// of the two the Agency counts from, and the ticket's own escape hatch for that unknown is to read from
    /// wherever the pilot actually is right now and label the line accordingly (AC-3).
    /// </summary>
    private async Task<(string? Text, string? EmptyText)> _JumpsAsync(
        ActivityDetailDto detail, CancellationToken cancellationToken)
    {
        if (_DestinationOf(detail) is not { } typed
            || !int.TryParse(typed, CultureInfo.InvariantCulture, out int destinationSystemId))
            return (null, null);

        if (services.Esi is null || services.Locations is null)
            return (null, "Jump count not available: no ESI connection.");

        if (detail.Runs.FirstOrDefault()?.CharacterId is not { } characterId)
            return (null, "Jump count not available: no character recorded on this run.");

        var location = await services.Locations.GetLocationAsync(checked((int)characterId), cancellationToken);
        if (location is not { IsSuccess: true, Value: { } here })
            return (null, "Jump count not available: the pilot's current location could not be read.");

        var route = await services.Esi.GetAsync<int[]>($"/route/{here.SolarSystemId}/{destinationSystemId}/",
            cancellationToken: cancellationToken, expectedNotFound: true);
        return route is { IsSuccess: true, Value.Length: > 0 }
            ? ($"{route.Value.Length - 1} jumps from here", null)
            : (null, "Jump count not available: no stargate route to this system.");
    }
}
