namespace EveUtils.Shared.Identity;

/// <summary>
/// Manages the set of known local characters. There is no "active character": actions
/// pick a character explicitly at the moment of the action.
/// </summary>
public interface ICharacterRegistry
{
    /// <summary>Adds or replaces the character entry (keyed by <see cref="Character.EsiCharacterId"/>). A new
    /// character appends to the end of the user-defined order; an existing one keeps its position.</summary>
    Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default);

    /// <summary>All known characters in the user-defined order (see <see cref="ReorderAsync"/>).</summary>
    Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default);

    Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Persists a new character order. <paramref name="orderedEsiCharacterIds"/> lists the character ids in
    /// the desired top-to-bottom order; any character not listed keeps a position after the listed ones.</summary>
    Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default);

    /// <summary>Raised when the character list changes.</summary>
    event Action RegistryChanged;
}
