using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="ServerAuthRepository.FindAllowedAsync"/> matches the allow-list server-side (B12): the EF predicate runs
/// in SQLite, including a <c>lower()</c> name compare, instead of materializing the whole list and filtering in memory
/// (an unbounded memory/DoS vector on the pairing path). The case-insensitive match is the core of the fix — it proves
/// the EF translation of <c>lower()</c> actually executes against the database.
/// </summary>
public sealed class ServerAuthRepositoryTests : IDisposable
{
    private readonly SqliteServerDbContextFactory _factory = new();

    private async Task<ServerAuthRepository> SeededRepositoryAsync(CancellationToken cancellationToken)
    {
        var repository = new ServerAuthRepository(_factory);
        await repository.AddAllowedAsync(new AllowedCharacter { CharacterName = "Jita Trader", EsiCharacterId = 90000001 }, cancellationToken);
        await repository.AddAllowedAsync(new AllowedCharacter { CharacterName = "Amarr Knight", EsiCharacterId = 90000002 }, cancellationToken);
        return repository;
    }

    [Fact]
    public async Task FindAllowedAsync_ExactName_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await SeededRepositoryAsync(ct);

        var match = await repository.FindAllowedAsync(esiCharacterId: null, "Jita Trader", ct);

        Assert.NotNull(match);
        Assert.Equal("Jita Trader", match!.CharacterName);
    }

    [Fact]
    public async Task FindAllowedAsync_DifferentCasing_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await SeededRepositoryAsync(ct);

        // The seeded name is "Jita Trader"; the lower() compare must match regardless of the caller's casing.
        var match = await repository.FindAllowedAsync(esiCharacterId: null, "jITA tRADER", ct);

        Assert.NotNull(match);
        Assert.Equal("Jita Trader", match!.CharacterName);
    }

    [Fact]
    public async Task FindAllowedAsync_ByEsiCharacterId_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await SeededRepositoryAsync(ct);

        // A name that does not match any row, but the id does → still found via the id branch of the predicate.
        var match = await repository.FindAllowedAsync(esiCharacterId: 90000002, "Someone Else Entirely", ct);

        Assert.NotNull(match);
        Assert.Equal("Amarr Knight", match!.CharacterName);
    }

    [Fact]
    public async Task FindAllowedAsync_NoMatch_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await SeededRepositoryAsync(ct);

        var match = await repository.FindAllowedAsync(esiCharacterId: 90009999, "Nobody Here", ct);

        Assert.Null(match);
    }

    public void Dispose() => _factory.Dispose();
}
