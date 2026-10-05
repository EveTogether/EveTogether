using System;

namespace EveUtils.Client.Esi;

/// <summary>The EVE login page reported that the access was not authorized: the user cancelled there.</summary>
public sealed class EsiSignInDeniedException()
    : Exception($"EVE SSO returned an error: {AccessDenied}")
{
    public const string AccessDenied = "access_denied";
}
