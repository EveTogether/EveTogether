namespace EveUtils.Shared.Data;

/// <summary>
/// Marks a syncable entity that carries owner attribution (foundation pillar 4). v1 stamps the
/// single local owner; v2 scopes queries per principal. <see cref="OwnerId"/> is attribution
/// metadata, not a security boundary.
/// </summary>
public interface IOwnedEntity
{
    string OwnerId { get; set; }
}
