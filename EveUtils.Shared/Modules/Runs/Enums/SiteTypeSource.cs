namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Which id space <see cref="Entities.Run.SiteTypeId"/> was taken from. Site and mission ids are disjunct
/// spaces that reuse the same numbers, so the id on its own cannot say what it points at (ET-137).</summary>
public enum SiteTypeSource
{
    Site,
    Mission
}
