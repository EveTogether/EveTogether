namespace EveUtils.Client.ViewModels.Setup;

/// <summary>Where the EVE sign-in of the character step stands.</summary>
public enum CharacterSignInState
{
    Idle,
    Waiting,

    /// <summary>The EVE login page reported <c>access_denied</c>.</summary>
    Cancelled,

    /// <summary>Nothing came back from the browser within the sign-in window.</summary>
    NoAnswer,

    /// <summary>Any other failure; the message says what.</summary>
    Failed
}
