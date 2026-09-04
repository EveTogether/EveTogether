using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>One role, one holder — written once here rather than in each handler that hands a role out, so a second
/// starting cargo hold stays impossible instead of becoming something to catch.</summary>
internal static class RunLootCaptureRoles
{
    /// <summary>Whoever held the role gives it up in the same write, and becomes an ordinary moment during the run
    /// rather than disappearing: it is still a cargo hold that was really copied, and the strip goes on saying so.
    /// Does not save — the caller's own <c>SaveChangesAsync</c> is what makes the swap one write.</summary>
    public static async Task AssignAsync(ClientDbContext db, RunLootCapture capture, LootCaptureRole role,
        CancellationToken cancellationToken)
    {
        if (role is not LootCaptureRole.Snapshot)
        {
            List<RunLootCapture> holders = await db.Set<RunLootCapture>()
                .Where(other => other.RunId == capture.RunId && other.Role == role && other.Id != capture.Id)
                .ToListAsync(cancellationToken);
            foreach (RunLootCapture holder in holders)
                holder.Role = LootCaptureRole.Snapshot;
        }

        capture.Role = role;
    }
}
