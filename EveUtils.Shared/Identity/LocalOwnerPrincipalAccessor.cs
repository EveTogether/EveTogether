namespace EveUtils.Shared.Identity;

/// <summary>
/// Client-side principal: a single local owner ("local"). The owned character is null until the user
/// signs in locally (ESI Mode A), after which <see cref="SetCharacter"/> fills it in — no login is
/// required to use the app offline.
/// </summary>
public sealed class LocalOwnerPrincipalAccessor : IPrincipalAccessor, ILocalPrincipalController
{
    private const string LocalOwnerId = "local";
    private volatile Principal _current = new(LocalOwnerId, null);

    public Principal Current => _current;

    public void SetCharacter(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);
        _current = _current with { Character = character };
    }
}
