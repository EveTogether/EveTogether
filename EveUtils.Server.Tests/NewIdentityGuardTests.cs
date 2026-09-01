using EveUtils.Server.Auth;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The startup gate on a freshly generated token-protector key (ET-94). Only a generated key next to characters
/// that are already paired is a refusal: a loaded key is normal, a genuinely first start has an empty table, and an
/// operator who means it says so with --accept-new-identity.
/// </summary>
public class NewIdentityGuardTests
{
    [Fact]
    public void ShouldRefuseStart_NewKeyWithPairedCharacters_Refuses()
    {
        Assert.True(NewIdentityGuard.ShouldRefuseStart(
            keyWasCreated: true, syncedCharacterCount: 3, newIdentityAccepted: false));
    }

    [Fact]
    public void ShouldRefuseStart_NewKeyWithoutPairedCharacters_Starts()
    {
        Assert.False(NewIdentityGuard.ShouldRefuseStart(
            keyWasCreated: true, syncedCharacterCount: 0, newIdentityAccepted: false));
    }

    [Fact]
    public void ShouldRefuseStart_LoadedKeyWithPairedCharacters_Starts()
    {
        Assert.False(NewIdentityGuard.ShouldRefuseStart(
            keyWasCreated: false, syncedCharacterCount: 3, newIdentityAccepted: false));
    }

    [Fact]
    public void ShouldRefuseStart_NewIdentityAccepted_Starts()
    {
        Assert.False(NewIdentityGuard.ShouldRefuseStart(
            keyWasCreated: true, syncedCharacterCount: 3, newIdentityAccepted: true));
    }

    [Fact]
    public void RefusalMessage_NamesTheDataDirectoryAndTheEscapeHatch()
    {
        var message = NewIdentityGuard.RefusalMessage("/data", syncedCharacterCount: 3);

        Assert.Contains("/data", message);
        Assert.Contains("token-protector.key", message);
        Assert.Contains(NewIdentityGuard.AcceptSwitch, message);
    }
}
