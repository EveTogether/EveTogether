namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// A fit as a list row: id, name and the resolved ship name + hull class. <c>Scope</c> is "local" (your own library)
/// or "server" (a coupled server's shared library, with <c>ServerName</c> set and <c>SharedBy</c> the pilot who
/// shared it). For a server fit the id is the server's shared-fit id — fetch its detail via
/// <c>GET /api/v1/fits/{id}?server=&lt;address&gt;</c>.
/// </summary>
public sealed record FitSummaryDto(
    int Id,
    string Name,
    int ShipTypeId,
    string ShipName,
    string? HullClass,
    string Scope,
    string? ServerAddress,
    string? ServerName,
    string? SharedBy = null);
