using Avalonia.Headless.XUnit;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Commands;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The owner-or-<c>fleet-composition.manage</c> gate, exercised by constructing the handlers over a
/// stub access policy — the v1 OwnerAllPolicy can't refuse, so a refusing stub is what proves the gate denies.
/// Red-without-fix: drop the <c>CanManageAsync</c> check in a mutation handler and the stranger edit/remove below
/// returns success instead of PERMISSION_DENIED.
/// </summary>
public class FleetCompositionAuthorizationTests
{
    private const int Owner = 95300001;
    private const int Stranger = 95300999;

    private static FleetCompositionAuthorizer Authorizer(bool policyGrants) =>
        new(new StubAccessPolicy(policyGrants), new StubPrincipalAccessor(new Principal("local", null)));

    private static async Task<(IFleetCompositionRepository Repo, long CompositionId)> SeedAsync(TestClientInstance instance)
    {
        var repo = instance.Services.GetRequiredService<IFleetCompositionRepository>();
        var now = DateTimeOffset.UtcNow;
        var id = await repo.AddAsync(new FleetComposition { Name = "Owned", OwnerCharacterId = Owner, CreatedAt = now, UpdatedAt = now });
        return (repo, id);
    }

    [AvaloniaFact]
    public async Task Edit_NonOwner_WithoutManageRight_IsDenied()
    {
        using var instance = TestClientInstance.Create();
        var (repo, compositionId) = await SeedAsync(instance);

        var handler = new EditFleetCompositionCommandHandler(repo, Authorizer(policyGrants: false));
        var result = await handler.Handle(new EditFleetCompositionCommand(compositionId, "Hijacked", null, Stranger));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Messages, m => m.Code == MessageCodes.PermissionDenied);
        Assert.Equal("Owned", (await repo.GetAsync(compositionId))!.Name); // unchanged
    }

    [AvaloniaFact]
    public async Task Edit_Owner_IsAllowed_EvenWhenPolicyRefuses()
    {
        using var instance = TestClientInstance.Create();
        var (repo, compositionId) = await SeedAsync(instance);

        // The owner branch holds regardless of the refusing policy.
        var handler = new EditFleetCompositionCommandHandler(repo, Authorizer(policyGrants: false));
        var result = await handler.Handle(new EditFleetCompositionCommand(compositionId, "Renamed", null, Owner));

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", (await repo.GetAsync(compositionId))!.Name);
    }

    [AvaloniaFact]
    public async Task Edit_NonOwner_WithManageRight_IsAllowed()
    {
        using var instance = TestClientInstance.Create();
        var (repo, compositionId) = await SeedAsync(instance);

        var handler = new EditFleetCompositionCommandHandler(repo, Authorizer(policyGrants: true));
        var result = await handler.Handle(new EditFleetCompositionCommand(compositionId, "Managed", null, Stranger));

        Assert.True(result.IsSuccess);
        Assert.Equal("Managed", (await repo.GetAsync(compositionId))!.Name);
    }

    [AvaloniaFact]
    public async Task RemoveRole_NonOwner_WithoutManageRight_IsDenied_ViaResolutionChain()
    {
        using var instance = TestClientInstance.Create();
        var (repo, compositionId) = await SeedAsync(instance);
        var roleId = await repo.AddRoleAsync(new FleetCompositionRole { CompositionId = compositionId, RoleName = "DPS", SortOrder = 0 });

        var handler = new RemoveFleetCompositionRoleCommandHandler(repo, Authorizer(policyGrants: false));
        var result = await handler.Handle(new RemoveFleetCompositionRoleCommand(roleId, Stranger));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Messages, m => m.Code == MessageCodes.PermissionDenied);
        Assert.Single(await repo.ListRolesAsync(compositionId)); // role survived (role → composition → owner gate held)
    }
}
