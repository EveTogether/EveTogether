using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet;

namespace EveUtils.Client.Esi;

/// <summary>
/// <see cref="IEsiFleetClient"/> over the shared ESI pivot (<see cref="IEsiClient"/>): the pivot applies the compat
/// date, ETag/304 cache and error-limit budget, so this only shapes the fleet calls + enforces the boss precheck.
/// Singleton.
/// </summary>
public sealed class EsiFleetClient(IEsiClient esi) : IEsiFleetClient, ISingletonService
{
    private static readonly IReadOnlyList<string> ReadScopes = [FleetsScopeCatalog.ReadFleet];
    private static readonly IReadOnlyList<string> WriteScopes = [FleetsScopeCatalog.WriteFleet];

    public Task<EsiResult<EsiCharacterFleet>> GetCharacterFleetAsync(int characterId, CancellationToken cancellationToken = default) =>
        // A 404 here is the routine "character is not in a fleet" answer (the 60s self-report poll hits it constantly
        // for a member who isn't in-game), so flag it as expected → the pivot logs it at Debug, not Warning.
        esi.GetAsync<EsiCharacterFleet>($"/characters/{characterId}/fleet/", characterId, ReadScopes, cancellationToken,
            expectedNotFound: true);

    public async Task<EsiResult<EsiFleetMember[]>> GetMembersAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult<EsiFleetMember[]>.Fail(denied);
        return await esi.GetAsync<EsiFleetMember[]>($"/fleets/{fleetId}/members/", actingCharacterId, ReadScopes, cancellationToken);
    }

    public async Task<EsiResult<EsiFleetWing[]>> GetWingsAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult<EsiFleetWing[]>.Fail(denied);
        return await esi.GetAsync<EsiFleetWing[]>($"/fleets/{fleetId}/wings/", actingCharacterId, ReadScopes, cancellationToken);
    }

    public async Task<EsiResult> SetFleetSettingsAsync(long fleetId, int actingCharacterId, string? motd, bool? isFreeMove,
        CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult.Fail(denied);
        var body = JsonSerializer.Serialize(new FleetSettingsBody { Motd = motd, IsFreeMove = isFreeMove });
        return await esi.RequestAsync<object?>(
            EsiRequest.Put($"/fleets/{fleetId}/", body, actingCharacterId, WriteScopes), cancellationToken);
    }

    public async Task<EsiResult> MoveMemberAsync(long fleetId, int memberCharacterId, string role, long? wingId, long? squadId,
        int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult.Fail(denied);
        var body = JsonSerializer.Serialize(new FleetMemberMoveBody { Role = role, WingId = wingId, SquadId = squadId });
        return await esi.RequestAsync<object?>(
            EsiRequest.Put($"/fleets/{fleetId}/members/{memberCharacterId}/", body, actingCharacterId, WriteScopes), cancellationToken);
    }

    public async Task<EsiResult> KickMemberAsync(long fleetId, int memberCharacterId, int actingCharacterId,
        CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult.Fail(denied);
        return await esi.RequestAsync<object?>(
            EsiRequest.Delete($"/fleets/{fleetId}/members/{memberCharacterId}/", actingCharacterId, WriteScopes), cancellationToken);
    }

    public async Task<EsiResult<long>> CreateWingAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult<long>.Fail(denied);
        // ESI's create-wing takes no body and answers {"wing_id": N}; construct the body-less POST directly.
        var created = await esi.RequestAsync<WingCreated>(
            new EsiRequest($"/fleets/{fleetId}/wings/", HttpMethod.Post, actingCharacterId, WriteScopes), cancellationToken);
        return created is { IsSuccess: true, Value: { } wing }
            ? EsiResult<long>.Ok(wing.WingId, created.FromCache)
            : EsiResult<long>.Fail(created.Error ?? EsiError.Of(EsiErrorKind.ParseError, "ESI did not return a wing id."));
    }

    public async Task<EsiResult> RenameWingAsync(long fleetId, long wingId, string name, int actingCharacterId,
        CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult.Fail(denied);
        var body = JsonSerializer.Serialize(new FleetUnitNameBody { Name = name });
        return await esi.RequestAsync<object?>(
            EsiRequest.Put($"/fleets/{fleetId}/wings/{wingId}/", body, actingCharacterId, WriteScopes), cancellationToken);
    }

    public async Task<EsiResult<long>> CreateSquadAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult<long>.Fail(denied);
        // Route asymmetry: a squad is created nested under its wing but renamed on the flat /squads/{id}/.
        var created = await esi.RequestAsync<SquadCreated>(
            new EsiRequest($"/fleets/{fleetId}/wings/{wingId}/squads/", HttpMethod.Post, actingCharacterId, WriteScopes), cancellationToken);
        return created is { IsSuccess: true, Value: { } squad }
            ? EsiResult<long>.Ok(squad.SquadId, created.FromCache)
            : EsiResult<long>.Fail(created.Error ?? EsiError.Of(EsiErrorKind.ParseError, "ESI did not return a squad id."));
    }

    public async Task<EsiResult> RenameSquadAsync(long fleetId, long squadId, string name, int actingCharacterId,
        CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult.Fail(denied);
        var body = JsonSerializer.Serialize(new FleetUnitNameBody { Name = name });
        return await esi.RequestAsync<object?>(
            EsiRequest.Put($"/fleets/{fleetId}/squads/{squadId}/", body, actingCharacterId, WriteScopes), cancellationToken);
    }

    public async Task<EsiResult> DeleteWingAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult.Fail(denied);
        return await esi.RequestAsync<object?>(
            EsiRequest.Delete($"/fleets/{fleetId}/wings/{wingId}/", actingCharacterId, WriteScopes), cancellationToken);
    }

    public async Task<EsiResult> DeleteSquadAsync(long fleetId, long squadId, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult.Fail(denied);
        return await esi.RequestAsync<object?>(
            EsiRequest.Delete($"/fleets/{fleetId}/squads/{squadId}/", actingCharacterId, WriteScopes), cancellationToken);
    }

    public async Task<EsiResult> InviteMemberAsync(long fleetId, int characterId, string role, long? wingId, long? squadId,
        int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (await GuardBossAsync(fleetId, actingCharacterId, cancellationToken) is { } denied)
            return EsiResult.Fail(denied);
        var body = JsonSerializer.Serialize(new FleetInviteBody { CharacterId = characterId, Role = role, WingId = wingId, SquadId = squadId });
        return await esi.RequestAsync<object?>(
            EsiRequest.Post($"/fleets/{fleetId}/members/", body, actingCharacterId, WriteScopes), cancellationToken);
    }

    // PUT /fleets/{id}/ body — only the fields we're changing are written (ESI leaves the rest untouched).
    private sealed class FleetSettingsBody
    {
        [JsonPropertyName("motd")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Motd { get; set; }

        [JsonPropertyName("is_free_move")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsFreeMove { get; set; }
    }

    // PUT /fleets/{id}/members/{id}/ body — role is required; wing/squad only for the positions the role uses.
    private sealed class FleetMemberMoveBody
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;

        [JsonPropertyName("wing_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? WingId { get; set; }

        [JsonPropertyName("squad_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? SquadId { get; set; }
    }

    // PUT body for wing/squad rename — ESI takes a single {"name": "..."} field.
    private sealed class FleetUnitNameBody
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    // POST /fleets/{id}/members/ invite body — character_id + role are required; wing/squad only for the role's positions.
    private sealed class FleetInviteBody
    {
        [JsonPropertyName("character_id")] public int CharacterId { get; set; }
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;

        [JsonPropertyName("wing_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? WingId { get; set; }

        [JsonPropertyName("squad_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? SquadId { get; set; }
    }

    // POST create-wing/-squad responses — a single id field.
    private sealed class WingCreated
    {
        [JsonPropertyName("wing_id")] public long WingId { get; set; }
    }

    private sealed class SquadCreated
    {
        [JsonPropertyName("squad_id")] public long SquadId { get; set; }
    }

    // The fleet_boss_id precheck: /fleets/{id}/... is boss-only and ESI answers a non-boss with 404, which
    // burns the error-limit budget (ban risk). So verify via the cheap, 60s-cached per-member /characters/{id}/fleet/
    // that the acting character is in THIS fleet AND is its boss BEFORE hitting /fleets/{id}/. Returns the denial to
    // short-circuit, or null when the call is safe to send.
    private async Task<EsiError?> GuardBossAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken)
    {
        var charFleet = await GetCharacterFleetAsync(actingCharacterId, cancellationToken);
        if (!charFleet.IsSuccess || charFleet.Value is not { } fleet)
            return charFleet.Error ?? EsiError.Of(EsiErrorKind.NotFound, $"character {actingCharacterId} is not in a fleet");
        if (fleet.FleetId != fleetId)
            return EsiError.Of(EsiErrorKind.NotFound, $"character {actingCharacterId} is not in fleet {fleetId}");
        if (!fleet.IsBoss(actingCharacterId))
            return EsiError.Of(EsiErrorKind.NotFound,
                $"character {actingCharacterId} is not the boss of fleet {fleetId} — /fleets/{{id}}/ is boss-only (precheck, no call sent)");
        return null;
    }
}
