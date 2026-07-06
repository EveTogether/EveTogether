using EveUtils.Server.Auth;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Modules.AdminAuth.Permissions;
using EveUtils.Shared.Modules.AdminAuth.Repositories;
using EveUtils.Shared.Modules.AdminAuth.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the admin-panel auth foundation, runnable via <c>--admin-auth-test</c>.
/// Runs after the startup migration + seed, so it asserts the seeded roles/user, the PBKDF2 hasher, the
/// code-derived panel registry and the effective-permission resolution against the real DI container.
/// Exit code 0 = all checks passed, 1 = a check failed.
/// </summary>
public static class AdminAuthCheck
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils admin-auth foundation check ==");

        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAdminAuthRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IAdminPasswordHasher>();
        var registry = scope.ServiceProvider.GetRequiredService<IPermissionRegistry>();
        var ct = CancellationToken.None;
        var ok = true;

        // Seed: admin user.
        var admin = await repo.FindByNormalizedUsernameAsync("admin", ct);
        ok &= Check("seed: admin user exists", admin is not null);
        ok &= Check("seed: admin is active + must change password",
            admin is { IsActive: true, MustChangePassword: true });
        ok &= Check("seed: admin holds a super-admin role",
            admin?.UserRoles.Any(ur => ur.Role.IsSuperAdmin) == true);

        // Seed: roles.
        var roles = await repo.ListRolesAsync(ct);
        var administrator = roles.FirstOrDefault(r => r.Name == AdminAuthSeeder.AdministratorRole);
        var viewer = roles.FirstOrDefault(r => r.Name == AdminAuthSeeder.ViewerRole);
        ok &= Check("seed: Administrator role is super-admin", administrator is { IsSuperAdmin: true });
        ok &= Check("seed: Viewer role has exactly the .view codes",
            viewer is not null
            && viewer.Permissions.Select(p => p.PermissionCode).OrderBy(c => c)
                .SequenceEqual(PanelPermissions.All.Where(c => c.EndsWith(".view", StringComparison.Ordinal)).OrderBy(c => c)));

        // Hasher: round-trip + rejection + salted (two hashes of same input differ).
        var hash = hasher.Hash("admin");
        ok &= Check("hasher: verifies the correct password", hasher.Verify("admin", hash));
        ok &= Check("hasher: rejects a wrong password", !hasher.Verify("nope", hash));
        ok &= Check("hasher: salted (two hashes differ)", hasher.Hash("admin") != hash);
        ok &= Check("hasher: seeded hash verifies", admin is not null && hasher.Verify("admin", admin.PasswordHash));

        // Registry contains every panel code.
        ok &= Check("registry: contains all panel codes", PanelPermissions.All.All(registry.Contains));

        // Effective permissions: super-admin flag + one active super-admin.
        if (admin is not null)
        {
            var (isSuper, _) = await repo.GetEffectivePermissionsAsync(admin.Id, ct);
            ok &= Check("effective: admin resolves as super-admin", isSuper);
        }
        ok &= Check("protection: exactly one active super-admin seeded",
            await repo.CountActiveSuperAdminsAsync(ct) == 1);

        // ── Management protection rules ──────────────────────────────────────────────────────────────
        var management = scope.ServiceProvider.GetRequiredService<AdminManagementService>();
        var viewerRole = roles.FirstOrDefault(r => r.Name == AdminAuthSeeder.ViewerRole);
        if (admin is not null && administrator is not null && viewerRole is not null)
        {
            ok &= Check("mgmt: cannot deactivate yourself",
                !(await management.SetActiveAsync(admin.Id, admin.Id, false, ct)).Ok);
            ok &= Check("mgmt: cannot delete the last active super-admin",
                !(await management.DeleteUserAsync(999_999, admin.Id, ct)).Ok);
            ok &= Check("mgmt: cannot demote the last active super-admin",
                !(await management.SetUserRolesAsync(admin.Id, [viewerRole.Id], ct)).Ok);
            ok &= Check("mgmt: reject too-short password",
                !(await management.CreateUserAsync("tester", "short", [viewerRole.Id], ct)).Ok);
            ok &= Check("mgmt: cannot delete the last super-admin role",
                !(await management.DeleteRoleAsync(administrator.Id, ct)).Ok);

            var created = await management.CreateUserAsync("tester", "longenough1", [viewerRole.Id], ct);
            ok &= Check("mgmt: create a valid viewer user", created.Ok);
            var tester = await repo.FindByNormalizedUsernameAsync("tester", ct);
            ok &= Check("mgmt: created user forces password change", tester is { MustChangePassword: true });
            ok &= Check("mgmt: deactivate a non-last/non-self user",
                tester is not null && (await management.SetActiveAsync(admin.Id, tester.Id, false, ct)).Ok);
            if (tester is not null)
                await repo.DeleteUserAsync(tester.Id, ct); // cleanup
            ok &= Check("mgmt: admin still the sole super-admin after churn",
                await repo.CountActiveSuperAdminsAsync(ct) == 1);
        }

        // ── Data delete (fleet soft-disband via creator-gated command + raw purge) ───────────────────
        var data = scope.ServiceProvider.GetRequiredService<DataAdminService>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<EveUtils.Shared.Cqrs.IDispatcher>();
        var createFleet = await dispatcher.Send(new EveUtils.Shared.Modules.Fleet.Commands.CreateFleetCommand(
            "DataTest Fleet", null, EveUtils.Shared.Modules.Fleet.Entities.FleetVisibility.Public,
            null, null, EveUtils.Shared.Modules.Fleet.Entities.FleetOfflineBehavior.StayOffline, 4242), ct);
        if (createFleet.IsSuccess)
        {
            var fleetId = createFleet.Value;
            ok &= Check("data: disband sets fleet → Archived",
                await data.DisbandFleetAsync(fleetId, ct)
                && (await dispatcher.Query(new EveUtils.Shared.Modules.Fleet.Queries.GetFleetQuery(fleetId), ct))?.State
                    == EveUtils.Shared.Modules.Fleet.Entities.FleetState.Archived);
            await data.PurgeFleetAsync(fleetId, ct);
            ok &= Check("data: purge removes the fleet row",
                await dispatcher.Query(new EveUtils.Shared.Modules.Fleet.Queries.GetFleetQuery(fleetId), ct) is null);
        }
        else
        {
            ok &= Check("data: fleet create for delete test", false);
        }

        // ── Public-server-mode toggle ────────────────────────────────────────────────────────
        var toggles = scope.ServiceProvider.GetRequiredService<EveUtils.Shared.Modules.Permissions.Repositories.IPermissionToggleStore>();
        ok &= Check("toggle: allowed-list enforced by default",
            toggles.IsEnabled(EveUtils.Server.Auth.ServerToggles.AllowedListEnabled));
        toggles.SetEnabled(EveUtils.Server.Auth.ServerToggles.AllowedListEnabled, false);
        ok &= Check("toggle: public mode disables the allowed-list gate",
            !toggles.IsEnabled(EveUtils.Server.Auth.ServerToggles.AllowedListEnabled));
        toggles.SetEnabled(EveUtils.Server.Auth.ServerToggles.AllowedListEnabled, true);
        ok &= Check("toggle: re-enable restores enforcement",
            toggles.IsEnabled(EveUtils.Server.Auth.ServerToggles.AllowedListEnabled));

        // ── Claims/policy logic, super-admin bypass, seed idempotency, case-insensitivity, stamp ──
        if (admin is not null && viewerRole is not null)
        {
            var adminClaims = await AdminClaims.BuildAsync(repo, admin, ct);
            var adminPrincipal = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(adminClaims, "test"));
            ok &= Check("authz: super-admin bypasses every policy",
                adminPrincipal.IsSuperAdmin()
                && PanelPermissions.All.All(adminPrincipal.HasPanelPermission));

            await management.CreateUserAsync("viewer1", "viewerpass1", [viewerRole.Id], ct);
            var viewerUser = await repo.FindByNormalizedUsernameAsync("viewer1", ct);
            if (viewerUser is not null)
            {
                var viewerClaims = await AdminClaims.BuildAsync(repo, viewerUser, ct);
                var viewerPrincipal = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(viewerClaims, "test"));
                ok &= Check("authz: viewer has a granted .view code",
                    viewerPrincipal.HasPanelPermission(PanelPermissions.DashboardView));
                ok &= Check("authz: viewer lacks an ungranted .manage code",
                    !viewerPrincipal.HasPanelPermission(PanelPermissions.UsersManage));
                ok &= Check("authz: viewer is not super-admin", !viewerPrincipal.IsSuperAdmin());

                // Security-stamp changes when the user is promoted → revalidation forces a re-login.
                var before = await AdminClaims.ComputeStampAsync(repo, viewerUser, ct);
                await repo.SetUserRolesAsync(viewerUser.Id, [administrator!.Id], ct);
                var promoted = await repo.GetUserAsync(viewerUser.Id, ct);
                var after = await AdminClaims.ComputeStampAsync(repo, promoted!, ct);
                ok &= Check("revalidation: security stamp changes on role change", before != after);

                ok &= Check("uniqueness: username is case-insensitive",
                    !(await management.CreateUserAsync("VIEWER1", "anotherpass1", [viewerRole.Id], ct)).Ok);

                await repo.DeleteUserAsync(viewerUser.Id, ct); // cleanup
            }

            // Seed idempotency: a second seed adds no duplicate roles/users.
            var rolesBefore = (await repo.ListRolesAsync(ct)).Count;
            await AdminAuthSeeder.SeedAsync(repo, hasher, "admin", ct);
            ok &= Check("seed: idempotent (no duplicate roles)", (await repo.ListRolesAsync(ct)).Count == rolesBefore);
            ok &= Check("seed: idempotent (still one super-admin)", await repo.CountActiveSuperAdminsAsync(ct) == 1);
        }

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
