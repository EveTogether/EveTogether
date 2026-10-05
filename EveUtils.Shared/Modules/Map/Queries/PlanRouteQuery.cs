using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Shared.Modules.Map.Queries;

/// <summary>
/// A route between two systems over the local map (ET-392), offline and without asking ESI. No route is an expected
/// outcome — Pochven, the Jove regions and avoided space cut systems off — so it is a <c>ROUTE_NOT_FOUND</c> failure,
/// never an exception.
/// </summary>
/// <param name="Avoid">Bands the route may not pass through; the start and destination themselves are always allowed.</param>
/// <param name="SaferPenalty">What one jump into the unwanted space costs for Safer and LessSecure, against 1 for a jump
/// the preference likes.</param>
public sealed record PlanRouteQuery(
    int FromSystemId,
    int ToSystemId,
    RoutePreference Preference,
    IReadOnlySet<SecurityBand> Avoid,
    int SaferPenalty = PlanRouteQuery.DefaultSaferPenalty) : IQuery<Result<RouteDto>>
{
    /// <summary>A highsec detour of up to 50 jumps beats one jump through lowsec; gives the known 34-jump all-highsec
    /// Jita → Amarr route.</summary>
    public const int DefaultSaferPenalty = 50;
}
