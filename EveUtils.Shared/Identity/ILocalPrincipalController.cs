namespace EveUtils.Shared.Identity;

/// <summary>
/// Lets the client fill in the local principal's character once the user signs in (ESI Mode A).
/// Kept separate from <see cref="IPrincipalAccessor"/> so ordinary consumers only read the principal.
/// </summary>
public interface ILocalPrincipalController
{
    void SetCharacter(Character character);
}
