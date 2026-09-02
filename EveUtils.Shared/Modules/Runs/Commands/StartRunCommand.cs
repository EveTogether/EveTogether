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
    bool IsFleetCommander = false,
    // Announced to the fleet, not stored: the run row keeps SolarSystemId, and the pilot's window knows the system
    // only by the name its location sample carries.
    string? SolarSystemName = null,
    // Which id space SiteTypeId came from, plus the mission's agent and level (ET-137). Defaulted, because every
    // caller today starts a site or an abyssal run.
    SiteTypeSource SiteTypeSource = SiteTypeSource.Site,
    int? AgentId = null,
    int? MissionLevel = null) : ICommand<Result<Guid>>;
