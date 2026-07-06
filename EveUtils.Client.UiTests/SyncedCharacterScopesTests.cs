using EveUtils.Shared.Modules.ServerAuth.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The <see cref="SyncedCharacter.GrantedScopes"/> accessor over the JSON column (B10): a corrupt or whitespace
/// column must never throw in the middle of an authorization check — it degrades to "no scopes" (deny). Valid JSON
/// roundtrips, and assigning the property writes JSON the getter reads back.
/// </summary>
public class SyncedCharacterScopesTests
{
    [Fact]
    public void GrantedScopes_InvalidJson_ReturnsEmpty()
    {
        var character = new SyncedCharacter { GrantedScopesJson = "{not valid json" };
        Assert.Empty(character.GrantedScopes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GrantedScopes_BlankJson_ReturnsEmpty(string json)
    {
        var character = new SyncedCharacter { GrantedScopesJson = json };
        Assert.Empty(character.GrantedScopes);
    }

    [Fact]
    public void GrantedScopes_ValidJson_Roundtrips()
    {
        var character = new SyncedCharacter
        {
            GrantedScopesJson = "[\"esi-fleets.read_fleet.v1\",\"esi-location.read_location.v1\"]"
        };

        Assert.Equal(
            new[] { "esi-fleets.read_fleet.v1", "esi-location.read_location.v1" },
            character.GrantedScopes);
    }

    [Fact]
    public void GrantedScopes_SetterThenGetter_Roundtrips()
    {
        var scopes = new[] { "esi-skills.read_skills.v1", "esi-clones.read_implants.v1" };
        var character = new SyncedCharacter { GrantedScopes = scopes };

        Assert.Equal(scopes, character.GrantedScopes);
    }
}
