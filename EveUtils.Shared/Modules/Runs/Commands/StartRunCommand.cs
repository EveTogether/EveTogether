using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Commands;

public sealed record StartRunCommand(
    long CharacterId,
    ActivityKind ActivityKind,
    DateTime StartedAtUtc,
    int SiteTypeId,
    string? SiteName,
    int? SolarSystemId,
    string? GroupCode = null,
    string? Signature = null,
    RunRole Role = RunRole.Member,
    bool IsParticipant = true,
    bool IsPayoutEligible = true,
    string? FitContentHash = null,
    string? FitNameSnapshot = null,
    long? FleetId = null,
    bool IsFleetCommander = false) : ICommand<Result<Guid>>;
