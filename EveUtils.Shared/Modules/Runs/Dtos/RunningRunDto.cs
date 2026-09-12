using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <param name="StartedAtUtc">The stored anchor — the clock the window shows is this one, not a second one it kept.</param>
public sealed record RunningRunDto(
    Guid Id,
    long CharacterId,
    ActivityKind ActivityKind,
    DateTime StartedAtUtc,
    string? GroupCode,
    string? SiteName,
    /// <summary>The scan id this run was started from, e.g. RUS-326. Two runs of the same site are still two runs,
    /// and this is the only thing that tells them apart.</summary>
    string? Signature,
    /// <summary>The scanner's own group text for this site (ET-226) — carried so a window resuming an already
    /// running run can still show the right TYPE, not just one freshly copying a signature.</summary>
    string? SignatureGroupSnapshot = null,
    /// <summary>The dungeon id a single catalogue match resolved to at start (ET-228) — carried so a window
    /// adopting this run, or switching its own column onto it, keeps reading a homefront off the run's own stored
    /// fact (ET-268) rather than <c>MatchedSites</c>, which neither adopt nor switch has populated for this run.
    /// 0 when the run never resolved one, same as a fresh, unmatched copy.</summary>
    int SiteTypeId = 0,
    /// <summary>The mission facts a window used to forget the moment it was not the one that started the run
    /// (ET-252) — a second window, a restart, or the same mission copied again while one was already open all
    /// adopt this same row rather than being freshly told about it, and none of these four carried over: MISSION
    /// read the agent as unstated, the level and system as unknown, and the reward lines as never recorded, even
    /// though the run this row is had every one of them since the moment it started. Null/empty for anything that
    /// is not a mission.</summary>
    int? AgentId = null,
    int? MissionLevel = null,
    int? SolarSystemId = null,
    IReadOnlyList<RunParameterDto>? Parameters = null,
    /// <summary>Null while the run is still on the clock. Set when <see cref="Queries.GetRunningRunQuery.RunId"/>
    /// named this row explicitly (ET-254) — the one case this DTO may carry a row that is not actually running, so a
    /// window resuming a specific stopped run knows to come up paused rather than ticking, exactly as if its own
    /// pilot had pressed STOP and reopened it.</summary>
    DateTime? StoppedAtUtc = null);
