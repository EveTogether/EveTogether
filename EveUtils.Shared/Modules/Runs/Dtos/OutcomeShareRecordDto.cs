using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class OutcomeShareRecordDto
{
    public required string ServerAddress { get; init; }
    public required string ActivityRef { get; init; }
    public required long CharacterId { get; init; }
    public required OutcomeShareState State { get; init; }
    public long SourceRevision { get; init; }
    public long SentRevision { get; init; }
    public DateTime? SharedAtUtc { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }

    internal static OutcomeShareRecordDto FromEntity(OutcomeShareRecord record) => new()
    {
        ServerAddress = record.ServerAddress,
        ActivityRef = record.ActivityRef,
        CharacterId = record.CharacterId,
        State = record.State,
        SourceRevision = record.SourceRevision,
        SentRevision = record.SentRevision,
        SharedAtUtc = record.SharedAtUtc,
        Attempts = record.Attempts,
        LastError = record.LastError
    };

    internal void ToEntity(OutcomeShareRecord record)
    {
        record.ServerAddress = ServerAddress;
        record.ActivityRef = ActivityRef;
        record.CharacterId = CharacterId;
        record.State = State;
        record.SourceRevision = SourceRevision;
        record.SentRevision = SentRevision;
        record.SharedAtUtc = SharedAtUtc;
        record.Attempts = Attempts;
        record.LastError = LastError;
    }
}
