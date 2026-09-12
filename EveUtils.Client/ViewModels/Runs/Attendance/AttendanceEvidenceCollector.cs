using System;
using System.Collections.Generic;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>
/// What each character was seen doing to the site during one run (ET-230), summed from the moment the run started to
/// the moment it stopped — by the gamelog line's own time, so a line read late still lands on the right side of either.
///
/// Two kinds of source, and they are never mixed up: this client's own gamelog for its own characters, with EVE's own
/// thresholds (domain/homefronts.md §4.1: 1,000 damage or 1,000 repair; any remote capacitor, a salvaged wreck, a mining
/// cycle); and for anyone else only what their client put on the wire — damage or mining at all, "had activity this
/// run". Bounty is deliberately no evidence: the hauler outside the site gets bounty lines too (§4.2, measured).
/// Fed from the gamelog pump's thread and read from the window's, hence the lock.
/// </summary>
public sealed class AttendanceEvidenceCollector
{
    public const int DamageThreshold = 1000;
    public const int RepairThreshold = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<(long CharacterId, SiteContribution Kind), long> _contributions = [];
    private readonly Dictionary<long, int> _mined = [];
    private readonly HashSet<long> _fleetActivity = [];
    private DateTime? _fromUtc;
    private DateTime? _untilUtc;

    /// <summary>The run's own clock: nothing before its start or after its stop counts. A resumed run moves the stop
    /// back to null and counts on.</summary>
    public void SetWindow(DateTime? fromUtc, DateTime? untilUtc)
    {
        lock (_gate)
        {
            _fromUtc = fromUtc;
            _untilUtc = untilUtc;
        }
    }

    public void Note(long characterId, SiteContribution kind, int amount, DateTime atUtc)
    {
        lock (_gate)
        {
            if (!_IsWithinRun(atUtc) || amount <= 0)
                return;

            _contributions[(characterId, kind)] = _contributions.GetValueOrDefault((characterId, kind)) + amount;
        }
    }

    /// <summary>This run's own mined units for one character — read off the run itself (ET-229), so it is already
    /// bounded by the run and simply replaced.</summary>
    public void SetMined(long characterId, int units)
    {
        lock (_gate)
            _mined[characterId] = units;
    }

    /// <summary>Another pilot's client said this character dealt damage or mined, while the run was on.</summary>
    public void NoteFleetActivity(long characterId, DateTime atUtc)
    {
        lock (_gate)
            if (_IsWithinRun(atUtc))
                _fleetActivity.Add(characterId);
    }

    /// <summary>The strongest evidence there is for one character, or null for none at all.</summary>
    public AttendanceEvidence? Best(long characterId)
    {
        lock (_gate)
        {
            long damage = _contributions.GetValueOrDefault((characterId, SiteContribution.Damage));
            long repair = _contributions.GetValueOrDefault((characterId, SiteContribution.RemoteRepair));
            long capacitor = _contributions.GetValueOrDefault((characterId, SiteContribution.RemoteCapacitor));
            long salvaged = _contributions.GetValueOrDefault((characterId, SiteContribution.Salvage));
            int mined = _mined.GetValueOrDefault(characterId);

            if (damage >= DamageThreshold)
                return new AttendanceEvidence(AttendanceReason.DamageDealt, damage);
            if (repair >= RepairThreshold)
                return new AttendanceEvidence(AttendanceReason.RemoteRepair, repair);
            if (mined > 0)
                return new AttendanceEvidence(AttendanceReason.Mined, mined);
            if (salvaged > 0)
                return new AttendanceEvidence(AttendanceReason.Salvaged, salvaged);
            if (capacitor > 0)
                return new AttendanceEvidence(AttendanceReason.RemoteCapacitor, capacitor);
            return _fleetActivity.Contains(characterId) ? new AttendanceEvidence(AttendanceReason.FleetActivity, null) : null;
        }
    }

    private bool _IsWithinRun(DateTime atUtc) =>
        _fromUtc is { } from && atUtc >= from && (_untilUtc is not { } until || atUtc <= until);
}
