namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// A mission from the SDE (ET-173), name and keys only — the eight-language message blocks and reward fields make
/// up the bulk of <c>missions.jsonl</c>'s 53 MB raw and are not imported (data-minimalisation).
/// </summary>
/// <param name="KillMissionDungeonId">Lives in its own id space, disjunct from <c>Site.DungeonId</c> but for three
/// accidental overlaps (13341, 13342, 14100 — measured, build 3492266): never join it against the site
/// catalogue.</param>
/// <param name="ArcId">Set when the mission belongs to an epic arc (<c>epicArcs.jsonl</c>); null otherwise.</param>
public sealed record SdeMission(
    int MissionId,
    string Name,
    int? AgentTypeId,
    int? KillMissionDungeonId,
    int? ArcId);
