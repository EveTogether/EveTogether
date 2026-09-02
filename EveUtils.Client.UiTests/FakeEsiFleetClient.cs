using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Minimal <see cref="IEsiFleetClient"/> serving a configurable char-fleet; all write methods are unused.
/// <see cref="CharFleet"/> and <see cref="Error"/> are settable so a test can walk a fleet-boss handover the way
/// ESI reports one: the same character, a different answer on the next read.
/// </summary>
internal sealed class FakeEsiFleetClient : IEsiFleetClient
{
    public EsiCharacterFleet? CharFleet { get; set; }
    public EsiError? Error { get; set; }

    /// <summary>Counts the /characters/{id}/fleet/ reads, so a test can assert one is skipped — or not repeated
    /// faster than the endpoint's own cache.</summary>
    public int CharFleetReads { get; private set; }

    public Task<EsiResult<EsiCharacterFleet>> GetCharacterFleetAsync(int characterId, CancellationToken cancellationToken = default)
    {
        CharFleetReads++;
        return Task.FromResult(Error is { } e ? EsiResult<EsiCharacterFleet>.Fail(e)
            : CharFleet is { } f ? EsiResult<EsiCharacterFleet>.Ok(f)
            : EsiResult<EsiCharacterFleet>.Fail(EsiError.Of(EsiErrorKind.NotFound, "not in a fleet", 404)));
    }

    public Task<EsiResult<EsiFleetMember[]>> GetMembersAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult<EsiFleetMember[]>.Ok([]));
    public Task<EsiResult<EsiFleetWing[]>> GetWingsAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult<EsiFleetWing[]>.Ok([]));
    public Task<EsiResult> SetFleetSettingsAsync(long fleetId, int actingCharacterId, string? motd, bool? isFreeMove, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult.Ok());
    public Task<EsiResult> MoveMemberAsync(long fleetId, int memberCharacterId, string role, long? wingId, long? squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult.Ok());
    public Task<EsiResult> KickMemberAsync(long fleetId, int memberCharacterId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult.Ok());
    public Task<EsiResult<long>> CreateWingAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult<long>.Ok(0));
    public Task<EsiResult> RenameWingAsync(long fleetId, long wingId, string name, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult.Ok());
    public Task<EsiResult<long>> CreateSquadAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult<long>.Ok(0));
    public Task<EsiResult> RenameSquadAsync(long fleetId, long squadId, string name, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult.Ok());
    public Task<EsiResult> DeleteWingAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult.Ok());
    public Task<EsiResult> DeleteSquadAsync(long fleetId, long squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult.Ok());
    public Task<EsiResult> InviteMemberAsync(long fleetId, int characterId, string role, long? wingId, long? squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EsiResult.Ok());
}
