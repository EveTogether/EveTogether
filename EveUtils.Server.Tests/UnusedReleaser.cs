using EveUtils.Server.Auth;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EveUtils.Server.Tests;

internal static class UnusedReleaser
{
    public static SyncedCharacterReleaser Create(SqliteServerDbContextFactory factory) => new(
        new ServerAuthRepository(factory), new UnusedProtector(), new UnusedRevoker(),
        new EsiOptions { ClientId = "app-id" }, NullLogger<SyncedCharacterReleaser>.Instance);

    private sealed class UnusedProtector : ITokenProtector
    {
        public EncryptedToken Protect(string plaintext) => throw new NotSupportedException();

        public string Unprotect(EncryptedToken token) => throw new NotSupportedException();
    }

    private sealed class UnusedRevoker : IEsiTokenRevoker
    {
        public Task RevokeRefreshTokenAsync(string refreshToken, string clientId, string clientSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

internal sealed class UnusedDispatcher : IDispatcher
{
    public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Send(ICommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
