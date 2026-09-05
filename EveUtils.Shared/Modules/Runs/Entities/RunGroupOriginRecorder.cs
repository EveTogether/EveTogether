using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>Writes a <see cref="RunGroupOrigin"/> the first time a group code and its fleet are both known to this
/// client. A code is never reused for a different fleet, so the first writer's answer stands — later callers with
/// the same code are a no-op, not a correction.</summary>
internal static class RunGroupOriginRecorder
{
    public static async Task RecordAsync(
        ClientDbContext db, string groupCode, long fleetId, CancellationToken cancellationToken)
    {
        bool known = await db.Set<RunGroupOrigin>()
            .AnyAsync(origin => origin.GroupCode == groupCode, cancellationToken);
        if (known)
            return;

        db.Set<RunGroupOrigin>().Add(new RunGroupOrigin
        {
            GroupCode = groupCode,
            FleetId = fleetId,
            RecordedAtUtc = DateTime.UtcNow
        });
    }
}
