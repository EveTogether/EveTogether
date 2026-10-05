using System;
using EveUtils.Grpc;

namespace EveUtils.Client.Pairing;

/// <summary>A pairing the server refused or the user declined on the EVE page, with the reason the server gave.</summary>
public sealed class PairingFailedException(PairingFailure failure, string signedInCharacterName, string message)
    : Exception(message)
{
    public PairingFailure Failure { get; } = failure;

    /// <summary>On <see cref="PairingFailure.OtherCharacter"/>, the character that signed in instead.</summary>
    public string SignedInCharacterName { get; } = signedInCharacterName;
}
