using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.ApiKeys.Commands;
using EveUtils.Shared.Modules.ApiKeys.Dtos;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using EveUtils.Shared.Modules.ApiKeys.Queries;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using EveUtils.Shared.Modules.ApiKeys.Repositories.Implementations;
using EveUtils.Shared.Modules.ApiKeys.Services;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-118: creating, reading back, revoking and deleting a key. The rule these guard is that the plaintext
/// exists exactly once — in the reply to the create — and that nothing which reads a key afterwards can hand
/// out either the plaintext or the stored hash.
/// </summary>
public class ApiKeyModuleTests : IDisposable
{
    private readonly SqliteServerDbContextFactory _factory = new();
    private readonly IApiKeyRepository _repository;

    public ApiKeyModuleTests() => _repository = new ApiKeyRepository(_factory);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Create_StoresOnlyThePrefixAndTheHash()
    {
        NewApiKeyDto created = await _CreateAsync();

        ApiKey? stored = await _repository.GetAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(created.Prefix, stored.Prefix);
        Assert.DoesNotContain(created.PlainText, stored.SecretHash, StringComparison.Ordinal);
        Assert.True(ApiKeySecurity.TryParse(created.PlainText, out _, out var secret));
        Assert.Equal(stored.SecretHash, ApiKeySecurity.Hash(secret));
    }

    /// <summary>
    /// The counter-proof for "shown once": read the same key back through the query the panel uses and there is
    /// no field left that could carry it. Adding the hash to the DTO turns this red.
    /// </summary>
    [Fact]
    public async Task Get_ReturnsMetadataOnly_NeverThePlaintextOrTheHash()
    {
        NewApiKeyDto created = await _CreateAsync();
        ApiKey stored = await _repository.GetAsync(created.Id, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("not stored");

        ApiKeyDto? readBack = await new GetApiKeyQueryHandler(_repository).Handle(new GetApiKeyQuery(created.Id), TestContext.Current.CancellationToken);

        Assert.NotNull(readBack);
        Assert.Equal(created.Prefix, readBack.Prefix);
        var rendered = string.Join('|', typeof(ApiKeyDto).GetProperties()
            .Select(property => property.GetValue(readBack)?.ToString() ?? string.Empty));
        Assert.DoesNotContain(created.PlainText, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.SecretHash, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithoutALabel_Fails()
    {
        Result<NewApiKeyDto> result = await new CreateApiKeyCommandHandler(_repository)
            .Handle(new CreateApiKeyCommand("  ", ApiKeyScopes.All, "tests"), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.ValidationFailed, result.Messages[0].Code);
    }

    [Fact]
    public async Task Create_WithAnUnknownScope_Fails()
    {
        Result<NewApiKeyDto> result = await new CreateApiKeyCommandHandler(_repository)
            .Handle(new CreateApiKeyCommand("dashboard", ["write:all"], "tests"), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.ValidationFailed, result.Messages[0].Code);
    }

    [Fact]
    public async Task Revoke_SwitchesTheKeyOffButKeepsTheRow()
    {
        NewApiKeyDto created = await _CreateAsync();

        Result result = await new RevokeApiKeyCommandHandler(_repository).Handle(new RevokeApiKeyCommand(created.Id), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        ApiKey? stored = await _repository.GetAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.False(stored?.IsActive);
    }

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        NewApiKeyDto created = await _CreateAsync();

        Result result = await new DeleteApiKeyCommandHandler(_repository).Handle(new DeleteApiKeyCommand(created.Id), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(await _repository.GetAsync(created.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevokeAndDelete_OfAKeyThatIsNotThere_ReportNotFound()
    {
        Result revoke = await new RevokeApiKeyCommandHandler(_repository).Handle(new RevokeApiKeyCommand(404), TestContext.Current.CancellationToken);
        Result delete = await new DeleteApiKeyCommandHandler(_repository).Handle(new DeleteApiKeyCommand(404), TestContext.Current.CancellationToken);

        Assert.Equal(MessageCodes.NotFound, revoke.Messages[0].Code);
        Assert.Equal(MessageCodes.NotFound, delete.Messages[0].Code);
    }

    [Fact]
    public async Task SetScopes_ReplacesTheScopeSet_AndRefusesAnUnknownOne()
    {
        NewApiKeyDto created = await _CreateAsync();
        var handler = new SetApiKeyScopesCommandHandler(_repository);

        Result cleared = await handler.Handle(new SetApiKeyScopesCommand(created.Id, []), TestContext.Current.CancellationToken);
        Result rejected = await handler.Handle(new SetApiKeyScopesCommand(created.Id, ["read:fleets"]), TestContext.Current.CancellationToken);

        Assert.True(cleared.IsSuccess);
        Assert.Equal(string.Empty, (await _repository.GetAsync(created.Id, TestContext.Current.CancellationToken))?.Scopes);
        Assert.False(rejected.IsSuccess);
    }

    [Fact]
    public async Task List_ReturnsTheKeysNewestFirst()
    {
        await _CreateAsync("first");
        NewApiKeyDto second = await _CreateAsync("second");

        IReadOnlyList<ApiKeyDto> keys = await new ListApiKeysQueryHandler(_repository).Handle(new ListApiKeysQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(2, keys.Count);
        Assert.Equal(second.Id, keys[0].Id);
        Assert.Equal([ApiKeyScopes.ReadAll], keys[0].Scopes);
    }

    private async Task<NewApiKeyDto> _CreateAsync(string label = "dashboard")
    {
        Result<NewApiKeyDto> result = await new CreateApiKeyCommandHandler(_repository)
            .Handle(new CreateApiKeyCommand(label, ApiKeyScopes.All, "tests"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        return result.Value ?? throw new InvalidOperationException("the create returned no key");
    }
}
