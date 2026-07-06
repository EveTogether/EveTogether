using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Esi;

/// <summary>
/// ESI Mode A (local sign-in): runs the SSO with PKCE, catches the code on the fixed
/// loopback, exchanges it, validates the JWT (extracting the granted scope set), stores
/// the tokens encrypted per-character, and registers the character in the registry.
///
/// <para>
/// The base scope set is derived from <see cref="IEsiScopeRegistry"/> (all Client + Both requirements)
/// so modules declare their needs once at startup rather than hard-coding scope strings.
/// </para>
/// <para>
/// Call <see cref="SignInAsync(IReadOnlyList{string}?, CancellationToken)"/> to add a new character
/// or <see cref="ReAuthenticateAsync(int, IReadOnlyList{string}, CancellationToken)"/> to extend the
/// scope grant for an existing character without signing it out.
/// </para>
/// </summary>
public sealed class LocalEsiLoginService(
    EsiOptions options,
    IEsiAuthClient authClient,
    IEsiJwtValidator validator,
    IPerCharacterTokenStore tokenStore,
    ICharacterRegistry registry,
    IEsiScopeRegistry scopeRegistry) : ISingletonService
{
    /// <summary>
    /// Signs in a character requesting exactly <paramref name="requestedScopes"/> (the user picks these
    /// in the scope-selection dialog). <c>publicData</c> is always ensured. When null, the full
    /// client scope set from the registry is requested (all features).
    /// </summary>
    public async Task<EsiIdentity> SignInAsync(
        IReadOnlyList<string>? requestedScopes = null,
        CancellationToken cancellationToken = default)
    {
        var scopes = ResolveScopes(requestedScopes);
        var pkce = Pkce.Create();
        var state = Pkce.Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new LoopbackCallbackListener(options.CallbackUri);
        var callbackTask = listener.WaitForCallbackAsync(cancellationToken);

        OpenBrowser(BuildAuthorizeUrl(pkce, state, scopes));

        var callback = await callbackTask;
        if (callback.Error is not null)
            throw new InvalidOperationException($"EVE SSO returned an error: {callback.Error}");
        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth state mismatch (possible CSRF) — sign-in aborted.");
        if (string.IsNullOrEmpty(callback.Code))
            throw new InvalidOperationException("EVE SSO did not return an authorization code.");

        var tokens = string.IsNullOrEmpty(options.ClientSecret)
            ? await authClient.ExchangePublicAsync(callback.Code, pkce, options.ClientId, cancellationToken)
            : await authClient.ExchangePkceConfidentialAsync(callback.Code, pkce, options.ClientId, options.ClientSecret, cancellationToken);

        var identity = await validator.ValidateAsync(tokens.AccessToken, options.ClientId, cancellationToken);

        await tokenStore.SaveAsync(identity.CharacterId, tokens, cancellationToken);

        var character = new Character(identity.CharacterName, identity.CharacterId, identity.GrantedScopes);
        await registry.AddOrUpdateAsync(character, cancellationToken);

        return identity;
    }

    /// <summary>
    /// Re-authenticates an already-known character to add missing scopes.
    /// The existing scope set is merged with <paramref name="missingScopes"/> so no previously
    /// granted scope is lost.
    /// </summary>
    public async Task<EsiIdentity> ReAuthenticateAsync(
        int characterId,
        IReadOnlyList<string> missingScopes,
        CancellationToken cancellationToken = default)
    {
        var existingChar = (await registry.GetAllAsync(cancellationToken))
            .FirstOrDefault(c => c.EsiCharacterId == characterId);

        var currentScopes = existingChar?.GrantedScopes ?? [];
        var combined = currentScopes.Union(missingScopes, StringComparer.OrdinalIgnoreCase).ToList();

        return await SignInAsync(combined, cancellationToken);
    }

    private const string PublicDataScope = "publicData";

    private IReadOnlyList<string> ResolveScopes(IReadOnlyList<string>? requested)
    {
        // No explicit selection → request the full client scope set from the registry (all features).
        if (requested is null)
        {
            var registryScopes = scopeRegistry.GetScopes(EsiScopeTarget.Client);
            return registryScopes.Count > 0 ? registryScopes : options.Scopes;
        }

        // Explicit selection (from the dialog) → use exactly that, but always include publicData.
        return requested.Contains(PublicDataScope, StringComparer.OrdinalIgnoreCase)
            ? requested
            : [PublicDataScope, .. requested];
    }

    private string BuildAuthorizeUrl(Pkce pkce, string state, IReadOnlyList<string> scopes)
    {
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = options.CallbackUri,
            ["client_id"] = options.ClientId,
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = Pkce.Method
        };

        var query = string.Join('&', Map(parameters));
        return $"{EsiEndpoints.Authorize}?{query}";

        static IEnumerable<string> Map(Dictionary<string, string> p)
        {
            foreach (var (key, value) in p)
                yield return $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Console.WriteLine($"Open this URL to sign in:\n{url}");
        }
    }
}
