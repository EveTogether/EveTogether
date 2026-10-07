using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>
/// Where a killmail's victim died among the celestials of its system (ET-473): what the system map and the grid view
/// draw, and the "6 km from Stargate (Evati)" line. Distances are 3D, in metres, from the centre of each object.
/// </summary>
public sealed class KillmailLocation
{
    /// <summary>Within this distance an object counts as on the same grid. Chosen, not measured: grids are dynamic in
    /// the game, and this is roughly the size a pilot sees on one.</summary>
    public const double GridMetres = 8_000_000;

    private KillmailLocation(KillmailPosition loss, IReadOnlyList<(SdeCelestial Celestial, double Metres)> byDistance)
    {
        Loss = loss;
        ByDistance = byDistance;
    }

    public KillmailPosition Loss { get; }

    /// <summary>Every celestial of the system, nearest first.</summary>
    public IReadOnlyList<(SdeCelestial Celestial, double Metres)> ByDistance { get; }

    public SdeCelestial Nearest => ByDistance[0].Celestial;

    public double NearestMetres => ByDistance[0].Metres;

    /// <summary>Half the width of the grid view: a round distance that fits the nearest object with room around it,
    /// or a plain grid when nothing is near.</summary>
    public double GridHalfExtentMetres
    {
        get
        {
            double needed = Math.Max(Math.Min(NearestMetres, GridMetres) * 2.5, 8_000);
            return _gridSteps.FirstOrDefault(step => step >= needed, _gridSteps[^1]);
        }
    }

    private static readonly double[] _gridSteps =
        [10_000, 20_000, 50_000, 100_000, 200_000, 500_000, 1_000_000, 2_000_000, 5_000_000, 10_000_000, 20_000_000];

    /// <summary>"6 km from Stargate (Evati)", with "off grid" or "deep space" added when nothing is on grid.</summary>
    public string PlaceText(Func<string, string> markLocation)
    {
        string place = $"{SpaceDistance.Text(NearestMetres)} from {markLocation(Nearest.Name)}";
        return NearestMetres switch
        {
            <= GridMetres => place,
            < 0.1 * SpaceDistance.MetresPerAu => $"{place} · off grid",
            _ => $"{place} · deep space"
        };
    }

    /// <summary>Null when the system has no celestials to place the loss among — abyssal space, or no SDE.</summary>
    public static KillmailLocation? Create(IReadOnlyList<SdeCelestial> celestials, KillmailPosition loss)
    {
        if (celestials.Count == 0)
        {
            return null;
        }

        return new KillmailLocation(loss, [.. celestials
            .Select(celestial => (celestial, _Distance(celestial, loss)))
            .OrderBy(entry => entry.Item2)]);
    }

    private static double _Distance(SdeCelestial celestial, KillmailPosition point)
    {
        double dx = celestial.X - point.X, dy = celestial.Y - point.Y, dz = celestial.Z - point.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
