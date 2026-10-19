using EveUtils.Shared.Modules.Runs.Entities;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>The reference an activity goes by, <c>GroupCode ?? Id</c>: what an outcome is shared under, so a correction
/// finds the activity it belongs to again (ET-515).</summary>
internal static class ActivityRefs
{
    public static string Of(Run run) => run.GroupCode ?? run.Id.ToString();

    /// <summary>The runs of the named activities: a reference is a group code, or the id of a run without one.</summary>
    public static IQueryable<Run> Of(IQueryable<Run> runs, IReadOnlyCollection<string> activityRefs)
    {
        string[] groupCodes = [.. activityRefs];
        Guid[] runIds = [.. activityRefs.Select(reference => Guid.TryParse(reference, out Guid id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)];
        return runs.Where(run => run.GroupCode != null ? groupCodes.Contains(run.GroupCode) : runIds.Contains(run.Id));
    }
}
