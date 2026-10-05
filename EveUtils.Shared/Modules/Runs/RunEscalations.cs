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

    /// <summary>The run an escalation run was started from, and which of its escalations — null on any other run.</summary>
    public static (Guid SourceRunId, Guid? EntryId)? SourceOf(IEnumerable<RunParameterDto> parameters) =>
        parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationSourceRunId) is { } source
        && Guid.TryParse(source.TypedValue, out Guid sourceRunId)
            ? (sourceRunId, source.EntryId)
            : null;

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
