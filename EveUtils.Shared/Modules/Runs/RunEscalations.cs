using System.Globalization;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>Reads a run's escalations back out of its parameter rows (ET-451) — the one place that knows the rows of
/// one escalation share an <c>EntryId</c>, so the detail screen, the open-escalations list and the commands cannot
/// group them differently.</summary>
public static class RunEscalations
{
    public static IReadOnlyList<RunEscalationDto> Read(IEnumerable<RunParameterDto> parameters) =>
        [.. parameters
            .GroupBy(parameter => parameter.EntryId)
            .Select(_ReadEntry)
            .OfType<RunEscalationDto>()
            .OrderBy(escalation => escalation.RegisteredAtUtc)];

    /// <summary>The parameter rows of one newly registered escalation, sharing <paramref name="entryId"/> — the one
    /// place that decides which rows an escalation is made of, for the run window's SAVE and for registering after it
    /// (ET-453). The deadline is always the pilot's own reading, never a default duration (ET-125 AC-3).</summary>
    public static IReadOnlyList<RunParameterInput> Rows(
        Guid? entryId, string siteName, int? dungeonId, string destinationSystem, int? destinationSolarSystemId,
        DateTime expiresAtUtc, DateTime observedAtUtc)
    {
        RunParameterInput Row(RunParameterKey key, string value) =>
            new() { ParameterKey = key, TypedValue = value, EntryId = entryId, ObservedAtUtc = observedAtUtc };

        List<RunParameterInput> rows = [Row(RunParameterKey.Escalation, siteName)];
        if (dungeonId is { } dungeon)
            rows.Add(Row(RunParameterKey.EscalationDungeonId, dungeon.ToString(CultureInfo.InvariantCulture)));
        rows.Add(Row(RunParameterKey.EscalationSystem, destinationSystem));
        if (destinationSolarSystemId is { } solarSystem)
            rows.Add(Row(RunParameterKey.EscalationSolarSystemId, solarSystem.ToString(CultureInfo.InvariantCulture)));
        rows.Add(Row(RunParameterKey.EscalationExpiresAtUtc, expiresAtUtc.ToString("o", CultureInfo.InvariantCulture)));
        return rows;
    }

    /// <summary>The run an escalation run was started from, and which of its escalations — null on any other run.</summary>
    public static (Guid SourceRunId, Guid? EntryId)? SourceOf(IEnumerable<RunParameterDto> parameters) =>
        parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationSourceRunId) is { } source
        && Guid.TryParse(source.TypedValue, out Guid sourceRunId)
            ? (sourceRunId, source.EntryId)
            : null;

    /// <summary>Whether a run at this site flew the escalation: the dungeon id when both sides have one; a run that never
    /// resolved to a single dungeon (id 0) falls back to the name the pilot saw.</summary>
    public static bool IsSiteOf(RunEscalationDto escalation, int siteTypeId, string? siteName) =>
        escalation.DungeonId is > 0 && siteTypeId > 0
            ? escalation.DungeonId == siteTypeId
            : string.Equals(escalation.SiteName, siteName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The last moment a run can have started and still have flown the escalation: its deadline, or without
    /// one the <see cref="RunEscalationDto.UndatedLifetime"/> after it was registered.</summary>
    public static DateTime LastFlyableAtUtc(RunEscalationDto escalation) =>
        escalation.ExpiresAtUtc ?? escalation.RegisteredAtUtc + RunEscalationDto.UndatedLifetime;

    private static RunEscalationDto? _ReadEntry(IGrouping<Guid?, RunParameterDto> entry)
    {
        if (entry.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.Escalation) is not { } site)
            return null;

        string? Value(RunParameterKey key) =>
            entry.FirstOrDefault(parameter => parameter.ParameterKey == key)?.TypedValue;

        return new RunEscalationDto(
            entry.Key,
            site.TypedValue,
            _Int(Value(RunParameterKey.EscalationDungeonId)),
            Value(RunParameterKey.EscalationSystem) is { Length: > 0 } system ? system : null,
            _Int(Value(RunParameterKey.EscalationSolarSystemId)),
            DateTime.TryParse(Value(RunParameterKey.EscalationExpiresAtUtc), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime expiresAtUtc)
                ? expiresAtUtc
                : null,
            site.ObservedAtUtc,
            _Int(Value(RunParameterKey.EscalationOutcome)) is { } outcome && Enum.IsDefined((EscalationOutcome)outcome)
                ? (EscalationOutcome)outcome
                : null,
            Guid.TryParse(Value(RunParameterKey.EscalationCompletedByRunId), out Guid completedBy) ? completedBy : null);
    }

    private static int? _Int(string? stored) =>
        int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
}
