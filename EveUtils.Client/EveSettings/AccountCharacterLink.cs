namespace EveUtils.Client.EveSettings;

/// <summary>Where a link between an account and its characters came from — an inference and a fact the user stated
/// are not the same thing, and the screen says which one it is showing.</summary>
public enum AccountLinkOrigin
{
    /// <summary>Read out of EVE's own write times (see <see cref="EveSettingsLocator.DeriveAccountLinks"/>).</summary>
    Derived,

    /// <summary>The user said so. Never overwritten by a later inference.</summary>
    UserSet
}

/// <summary>
/// Which characters sit on one EVE account. EVE publishes this nowhere — not in the settings files, not over ESI —
/// so it is either inferred from the moment EVE wrote the files or stated by the user, and the origin travels with it.
/// A list, not a single character: an account holds up to three pilots.
/// </summary>
public sealed record AccountCharacterLink
{
    public required long AccountId { get; init; }

    public required IReadOnlyList<long> CharacterIds { get; init; }

    public required AccountLinkOrigin Origin { get; init; }

    /// <summary>When this link was first established, for the tooltip that explains where it came from.</summary>
    public DateTimeOffset? EstablishedAtUtc { get; init; }
}

/// <summary>
/// Folds newly derived links into the ones already remembered (ET-64).
///
/// Two rules, both there because the user is about to overwrite settings files on the strength of what this says:
/// a link the user stated themselves outranks anything inferred and is never rewritten, and a derivation that
/// contradicts what we already hold is dropped rather than resolved by picking a winner. Remembering also means a
/// later multiboxing session — six clients closed at once, every file stamped the same second — cannot wipe out a
/// link that a quieter evening made plain.
/// </summary>
public static class AccountLinkStore
{
    /// <summary>
    /// The stored links plus whatever <paramref name="derived"/> adds that does not conflict. Returns the merged set
    /// and whether it differs from <paramref name="stored"/>, so the caller only writes to disk when something is
    /// actually new.
    /// </summary>
    public static (IReadOnlyDictionary<long, AccountCharacterLink> Links, bool Changed) Merge(
        IReadOnlyDictionary<long, AccountCharacterLink> stored,
        IReadOnlyDictionary<long, IReadOnlyList<long>> derived,
        DateTimeOffset now)
    {
        var merged = stored.ToDictionary(pair => pair.Key, pair => pair.Value);

        // Every character already spoken for, and by whom: a character lives on exactly one account, so a derivation
        // that puts a known character on a different account is evidence that one of the two is wrong — and we do not
        // know which. It is dropped.
        var claimed = new Dictionary<long, long>();
        foreach (var link in merged.Values)
        {
            foreach (var characterId in link.CharacterIds)
                claimed[characterId] = link.AccountId;
        }

        var changed = false;
        foreach (var (accountId, characterIds) in derived.OrderBy(pair => pair.Key))
        {
            if (merged.TryGetValue(accountId, out var existing) && existing.Origin == AccountLinkOrigin.UserSet)
                continue;   // the user said so; nothing inferred gets to argue

            var additions = characterIds
                .Where(id => !claimed.TryGetValue(id, out var owner) || owner == accountId)
                .Where(id => existing is null || !existing.CharacterIds.Contains(id))
                .ToList();
            if (additions.Count == 0)
                continue;

            merged[accountId] = new AccountCharacterLink
            {
                AccountId = accountId,
                CharacterIds = (existing?.CharacterIds ?? []).Concat(additions).OrderBy(id => id).ToList(),
                Origin = AccountLinkOrigin.Derived,
                EstablishedAtUtc = existing?.EstablishedAtUtc ?? now
            };
            foreach (var id in additions)
                claimed[id] = accountId;
            changed = true;
        }

        return (merged, changed);
    }
}
