namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// A doctrine/composition as a library row. <c>Scope</c> is "local" (your own local library) or "server" (a coupled
/// server's library, with <c>ServerName</c> set). <c>FleetCount</c> is how many fleets are coupled to it. Fetch its
/// roles + fits via <c>GET /api/v1/compositions/{id}</c> (pass <c>?server=</c> for a server composition).
/// </summary>
public sealed record CompositionListItemDto(
    long Id,
    string Name,
    string? Description,
    string Scope,
    string? ServerAddress,
    string? ServerName,
    int OwnerCharacterId,
    string OwnerName,
    int FleetCount);
