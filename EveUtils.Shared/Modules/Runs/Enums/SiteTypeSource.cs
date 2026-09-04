namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Which id space <see cref="Entities.Run.SiteTypeId"/> was taken from. Site and mission ids are disjunct
/// spaces that reuse the same numbers, so the id on its own cannot say what it points at (ET-137).</summary>
public enum SiteTypeSource
{
    Site,
    Mission,

    /// <summary>A named site the SDE catalogue does not carry at all — Data Site, Relic Site, Wormhole and the like
    /// (ET-178). Still has a source, the pilot's own clipboard; only the catalogue is missing, which is what sets
    /// this apart from a site never named at all. Appended rather than inserted, so a persisted 0/1 keeps meaning
    /// Site/Mission.</summary>
    Uncatalogued
}
