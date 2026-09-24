using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Killmails;

/// <summary>A run as far as linking a loss to it goes; <see cref="ShipTypeId"/> is the hull of its fit, null when the
/// run has no fit or the fit is not in the library.</summary>
public sealed record LinkableRun(
    Guid Id, long CharacterId, ActivityKind ActivityKind, int? SolarSystemId, DateTime StartedAtUtc, DateTime? StoppedAtUtc,
    int? ShipTypeId);

/// <summary>The outcome of the link rule for one loss: the run when exactly one fits, and how many fitted either way.</summary>
public sealed record KillmailRunMatch(Guid? RunId, int CandidateCount);

/// <summary>
/// The rule that links a character's own loss to the run it happened in (ET-331). Pure: it reads the loss, the
/// character's runs and its already linked ship losses, and decides nothing it was not handed.
/// </summary>
public static class KillmailRunLinker
{
    /// <summary>How long after a run's stop a loss still belongs to it: the run stops at the next location poll.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromMinutes(2);

    /// <summary>How long after its ship a capsule loss still follows that ship's run.</summary>
    public static readonly TimeSpan CapsuleGrace = TimeSpan.FromSeconds(60);

    public static bool IsCapsule(int shipTypeId) => shipTypeId is 670 or 33328;

    /// <summary>
    /// The run <paramref name="loss"/> belongs to. A capsule follows the latest linked ship loss up to
    /// <see cref="CapsuleGrace"/> before it and is otherwise left unlinked; a ship needs exactly one fitting run.
    /// </summary>
    public static KillmailRunMatch Match(LocalKillmail loss, IReadOnlyList<LinkableRun> runs,
        IReadOnlyList<LocalKillmail> linkedShipLosses, DateTime nowUtc)
    {
        if (IsCapsule(loss.VictimShipTypeId))
        {
            Guid? shipRunId = linkedShipLosses
                .Where(ship => ship.CharacterId == loss.CharacterId && ship.RunId is not null
                               && !IsCapsule(ship.VictimShipTypeId)
                               && ship.KillmailTimeUtc <= loss.KillmailTimeUtc
                               && loss.KillmailTimeUtc - ship.KillmailTimeUtc <= CapsuleGrace)
                .MaxBy(ship => ship.KillmailTimeUtc)?.RunId;
            return shipRunId is null ? new KillmailRunMatch(null, 0) : new KillmailRunMatch(shipRunId, 1);
        }

        LinkableRun[] candidates = [.. runs.Where(run => _Fits(loss, run, nowUtc))];
        return candidates.Length == 1
            ? new KillmailRunMatch(candidates[0].Id, 1)
            : new KillmailRunMatch(null, candidates.Length);
    }

    private static bool _Fits(LocalKillmail loss, LinkableRun run, DateTime nowUtc)
    {
        if (run.CharacterId != loss.CharacterId || loss.KillmailTimeUtc < run.StartedAtUtc
            || loss.KillmailTimeUtc > (run.StoppedAtUtc ?? nowUtc) + StopGrace)
        {
            return false;
        }

        // The abyss has no system worth comparing: any abyssal run of the time may be it, and nothing else can be.
        bool placeFits = AbyssalSpace.IsAbyssalSystem(loss.SolarSystemId)
            ? run.ActivityKind is ActivityKind.Abyssal
            : run.SolarSystemId is null || run.SolarSystemId == loss.SolarSystemId;
        return placeFits && (run.ShipTypeId is null || run.ShipTypeId == loss.VictimShipTypeId);
    }
}
