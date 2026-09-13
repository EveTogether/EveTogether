using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// Application per weapon, from every OUTGOING line (ET-277). Only the shots at the target the weapon is shooting now
/// are counted: application is about this target, and a switch starts the count again.
///
/// Turrets and drones are read from the hit-quality word: each word stands for a band of the turret damage roll, a shot
/// scores the middle of its band (a miss scores 0), and the average over the recent shots is the percentage of what the
/// weapon would do at a certain hit. A wreck counts as the top of the smash band, not ×3 — grouped turrets almost never
/// wreck (<c>domain/combat-gamelog.md</c>), so one lucky crit must not lift ten misses to a respectable figure.
///
/// Missiles always log "Hits", however they land, but they have no damage roll either (ET-282): each volley is read
/// against a full volley at that target from the character's <see cref="MissileGauge"/>. A volley is every line of the
/// launcher group within a second — the game splits one volley over lines when its missiles land a tick apart — and the
/// figure is the median volley, so the last volley on a dying target (logged as only the hit points it had left) and the
/// one that breaks through to a weaker layer do not move it.
///
/// Lock-guarded: the gamelog pump adds, the fleet sampler and the screens read.
/// </summary>
public sealed class WeaponApplicationTracker(Func<string, WeaponClass> classify, MissileGauge? missiles = null)
{
    /// <summary>How far back the shots at the current target are counted.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    /// <summary>The same for missiles: their volleys are far apart, so more time is needed for as many of them.</summary>
    public static readonly TimeSpan MissileWindow = TimeSpan.FromSeconds(30);

    /// <summary>Fewer shots than this at the current target make no verdict.</summary>
    public const int MinShots = 6;

    /// <summary>Fewer missile volleys than this at the current target make no verdict.</summary>
    public const int MinVolleys = 4;

    // Game-log times have one-second resolution: lines up to this long after a volley's first line are that volley.
    private static readonly TimeSpan VolleySpread = TimeSpan.FromSeconds(1);

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
    private readonly Dictionary<string, Volley> _openVolleys = new(StringComparer.Ordinal);
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

            if (_classes[weapon] is WeaponClass.Missile && missiles is not null)
                missiles.Learn(weapon, target, _AddToVolley(weapon, at, target, amount));
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
                var window = _classes[weapon] is WeaponClass.Missile ? MissileWindow : Window;
                while (shots.Count > 0 && now - shots.Peek().At > window)
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
        var breakdown = string.Join(" · ", weapons.Select(w =>
            $"{w.Weapon} {ApplicationSummary.Label(w.Verdict, w.Percent)}{(w.Cause is { } cause ? $" ({cause})" : string.Empty)}"));
        return new ApplicationSummary(main.Verdict, main.Percent, breakdown);
    }

    private WeaponApplication _Read(string weapon, WeaponClass weaponClass, Queue<Shot> shots)
    {
        var target = shots.Last().Target;
        var atTarget = shots.Where(shot => shot.Target == target).ToList();
        var damage = atTarget.Sum(shot => (long)shot.Amount);

        if (weaponClass is WeaponClass.Missile)
            return _ReadMissile(weapon, target, atTarget, damage);
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

    private WeaponApplication _ReadMissile(string weapon, string target, List<Shot> atTarget, long damage)
    {
        var reference = missiles?.Reference(weapon, target);
        var volleys = _Volleys(atTarget);
        WeaponApplication Unjudged(ApplicationVerdict verdict, string? cause = null) =>
            new(weapon, WeaponClass.Missile, target, volleys.Count, null, verdict, damage, cause);

        if (reference is { Target: null })
            return Unjudged(ApplicationVerdict.NotMeasurable, "not an NPC; its resists are unknown");
        if (reference?.FullDamage is not { } full)
            return Unjudged(ApplicationVerdict.Learning);
        if (volleys.Count < MinVolleys)
            return Unjudged(ApplicationVerdict.NotEnoughShots);

        // Capped per volley: one that reached a weaker layer reads full, not more, and cannot make up for another.
        var landed = volleys.Select(volley => Math.Min(1, volley / full)).Order().ToList();
        var median = landed.Count % 2 == 1
            ? landed[landed.Count / 2]
            : (landed[landed.Count / 2 - 1] + landed[landed.Count / 2]) / 2;
        var percent = 100 * median;
        return new WeaponApplication(weapon, WeaponClass.Missile, target, volleys.Count, percent,
            ApplicationSummary.VerdictFor(percent), damage, _MissileCause(reference, percent));
    }

    // Why a missile lands short, as far as the target's own numbers allow it: its signature caps it even standing still
    // (the wrong missile size, or a painter's job), it can outrun the explosion (a web's job), or both. A target that
    // can do neither cannot be what cost the damage: those volleys were cut short another way — the last one on a dying
    // target is logged as only the hit points it had left.
    private static string? _MissileCause(MissileReference reference, double percent)
    {
        if (percent >= ApplicationSummary.SweetSpotFrom)
            return null;

        var standing = 100 * reference.SignatureCeiling;
        var outrunsIt = 100 * reference.TopSpeedCeiling < Math.Min(standing, ApplicationSummary.SweetSpotFrom);
        if (standing < ApplicationSummary.SweetSpotFrom)
            return outrunsIt && percent < 0.9 * standing
                ? $"target too small and too fast: at most {standing:0}% even standing still; web and paint it, or use smaller missiles"
                : $"target too small: at most {standing:0}% even standing still; paint it or use smaller missiles";
        return outrunsIt
            ? "target too fast; web or paint it"
            : "volleys cut short, not by the target: kill shots on dying targets, or launchers reloading or out of the group";
    }

    private static List<double> _Volleys(List<Shot> shots)
    {
        var volleys = new List<double>();
        var start = DateTime.MinValue;
        foreach (var shot in shots)
        {
            if (volleys.Count == 0 || shot.At - start > VolleySpread)
            {
                volleys.Add(0);
                start = shot.At;
            }
            volleys[^1] += shot.Amount;
        }
        return volleys;
    }

    // The volley this line belongs to, as logged so far: what the gauge learns from. A part of a volley is never more
    // than the whole, so feeding it the running sum is safe for a best-volley rule.
    private double _AddToVolley(string weapon, DateTime at, string target, int amount)
    {
        var volley = _openVolleys.TryGetValue(weapon, out var open) && open.Target == target && at - open.Start <= VolleySpread
            ? open with { Damage = open.Damage + Math.Max(0, amount) }
            : new Volley(at, target, Math.Max(0, amount));
        _openVolleys[weapon] = volley;
        return volley.Damage;
    }

    private static int _Rank(WeaponClass weaponClass) => weaponClass switch
    {
        WeaponClass.Turret => 0,
        WeaponClass.Missile => 1,
        WeaponClass.Drone => 2,
        _ => 3,
    };

    private readonly record struct Shot(DateTime At, string Target, HitQuality Quality, int Amount);

    private readonly record struct Volley(DateTime Start, string Target, double Damage);
}
