using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// Application per weapon, from the hit-quality word on every OUTGOING line (ET-277). Each word stands for a band of
/// the turret damage roll; a shot scores the middle of its band (a miss scores 0) and the average over the recent shots
/// is the percentage of what the weapon would do at a certain hit. Only the shots at the target the weapon is shooting
/// now are counted: application is about this target, and a switch starts the count again.
///
/// Two deliberate choices, measured in <c>domain/combat-gamelog.md</c>: a wreck counts as the top of the smash band,
/// not ×3 — grouped turrets almost never wreck, so one lucky crit must not lift ten misses to a respectable figure — and
/// a missile is never given a percentage, because its word is always "Hits" however it lands.
///
/// Lock-guarded: the gamelog pump adds, the fleet sampler and the screens read.
/// </summary>
public sealed class WeaponApplicationTracker(Func<string, WeaponClass> classify)
{
    /// <summary>How far back the shots at the current target are counted.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    /// <summary>Fewer shots than this at the current target make no verdict.</summary>
    public const int MinShots = 6;

    // The middle of each word's damage band, as a fraction of the damage at a certain hit (the roll's expected value
    // at 100 % chance to hit is 1.0).
    private static double Score(HitQuality quality) => quality switch
    {
        HitQuality.Grazes => 0.5625,
        HitQuality.Glances => 0.6875,
        HitQuality.Hits => 0.875,
        HitQuality.Penetrates => 1.125,
        HitQuality.Smashes => 1.37,
        HitQuality.Wrecks => 1.49,
        _ => 0,
    };

    private readonly Dictionary<string, Queue<Shot>> _shots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeaponClass> _classes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Add(DateTime at, string weapon, string target, HitQuality quality, int amount)
    {
        if (string.IsNullOrWhiteSpace(weapon))
            return;

        lock (_gate)
        {
            if (!_shots.TryGetValue(weapon, out var shots))
            {
                _shots[weapon] = shots = new Queue<Shot>();
                _classes[weapon] = classify(weapon);
            }
            shots.Enqueue(new Shot(at, target, quality, Math.Max(0, amount)));
        }
    }

    /// <summary>Every weapon that fired inside the window, the main weapon first.</summary>
    public IReadOnlyList<WeaponApplication> Read(DateTime now)
    {
        lock (_gate)
        {
            var readings = new List<WeaponApplication>();
            foreach (var (weapon, shots) in _shots)
            {
                while (shots.Count > 0 && now - shots.Peek().At > Window)
                    shots.Dequeue();
                if (shots.Count == 0)
                    continue;

                // Asked again while unresolved: the SDE may not have been built yet when the weapon first fired.
                if (_classes[weapon] is WeaponClass.Unknown)
                    _classes[weapon] = classify(weapon);
                readings.Add(_Read(weapon, _classes[weapon], shots));
            }

            // The main weapon is the one fitted to do the damage: turrets when there are any, even while they only
            // miss (the 12 Sep laser groups missing a frigate while the drones hit it are exactly what must show).
            return readings
                .OrderBy(reading => _Rank(reading.Class))
                .ThenByDescending(reading => reading.Damage)
                .ThenByDescending(reading => reading.Shots)
                .ToList();
        }
    }

    /// <summary>The main weapon's verdict and a breakdown of all of them, as the screens and the fleet wire take it.</summary>
    public ApplicationSummary Summarize(DateTime now)
    {
        var weapons = Read(now);
        if (weapons.Count == 0)
            return ApplicationSummary.Idle;

        var main = weapons[0];
        var breakdown = string.Join(" · ", weapons.Select(w => $"{w.Weapon} {ApplicationSummary.Label(w.Verdict, w.Percent)}"));
        return new ApplicationSummary(main.Verdict, main.Percent, breakdown);
    }

    private static WeaponApplication _Read(string weapon, WeaponClass weaponClass, Queue<Shot> shots)
    {
        var target = shots.Last().Target;
        var atTarget = shots.Where(shot => shot.Target == target).ToList();
        var damage = atTarget.Sum(shot => (long)shot.Amount);

        if (weaponClass is WeaponClass.Missile)
            return new WeaponApplication(weapon, weaponClass, target, atTarget.Count, null, ApplicationVerdict.NotMeasurable, damage);
        if (atTarget.Count < MinShots)
            return new WeaponApplication(weapon, weaponClass, target, atTarget.Count, null, ApplicationVerdict.NotEnoughShots, damage);

        // Unresolved names get the same protection from the data itself: a weapon that has written nothing but "Hits"
        // is carrying no quality signal, whatever it turns out to be.
        if (weaponClass is WeaponClass.Unknown && atTarget.All(shot => shot.Quality is HitQuality.Hits))
            return new WeaponApplication(weapon, weaponClass, target, atTarget.Count, null, ApplicationVerdict.NotMeasurable, damage);

        var percent = Math.Min(100, 100 * atTarget.Average(shot => Score(shot.Quality)));
        return new WeaponApplication(weapon, weaponClass, target, atTarget.Count, percent,
            ApplicationSummary.VerdictFor(percent), damage);
    }

    private static int _Rank(WeaponClass weaponClass) => weaponClass switch
    {
        WeaponClass.Turret => 0,
        WeaponClass.Missile => 1,
        WeaponClass.Drone => 2,
        _ => 3,
    };

    private readonly record struct Shot(DateTime At, string Target, HitQuality Quality, int Amount);
}
