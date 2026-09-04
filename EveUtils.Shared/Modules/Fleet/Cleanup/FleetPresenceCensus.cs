using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Shared.Modules.Fleet.Cleanup;

/// <summary>
/// What a fleet's roster looks like from the presence side, at one instant — the only thing
/// <see cref="FleetAutoStopPolicy"/> needs to know about it. Counted here rather than in the policy so the rule
/// stays a pure decision table, and counted the same way on both hosts.
/// </summary>
/// <param name="MemberCount">Every roster row, externals included. This answers "did everyone leave", and an
/// external member is still someone on the roster.</param>
/// <param name="PresentCount">Members heard from within <see cref="FleetMemberPresence.SilentAfter"/>.</param>
/// <param name="EverHeardCount">Members ever heard from at all. ET-70's rule survives here: silence that was never
/// preceded by contact is not evidence, so a fleet nobody has ever published into cannot be read as emptied.</param>
public readonly record struct FleetPresenceCensus(int MemberCount, int PresentCount, int EverHeardCount)
{
    /// <summary>
    /// Counts a roster as of <paramref name="now"/>. External members are counted as members but never as evidence:
    /// they have no client on this server and so are permanently unheard by definition — reading their silence as
    /// departure would stop a fleet the moment it was started with one on the roster.
    /// </summary>
    public static FleetPresenceCensus Take(IEnumerable<FleetMember> members, DateTimeOffset now)
    {
        var total = 0;
        var present = 0;
        var everHeard = 0;

        foreach (var member in members)
        {
            total++;
            if (member.IsExternal || member.LastSeenAt is null)
                continue;

            everHeard++;
            if (!FleetMemberPresence.IsSilent(member.LastSeenAt, now))
                present++;
        }

        return new FleetPresenceCensus(total, present, everHeard);
    }
}
