namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// Attaches synthetic effects to every type in the given inventory categories (a by-category dogma patch).
/// Lets a patched effect — e.g. the ship-wide <c>velocityBoost</c> calculation — run on all ships
/// without touching the per-type SDE data. Category filtering only (cached lookup, no recursion); broaden if needed.
/// </summary>
public sealed record TypeLink(IReadOnlyList<int> Categories, IReadOnlyList<int> AttachEffectIds);
