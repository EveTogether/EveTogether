using System.Text.Json;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fittings.Services;

namespace EveUtils.Shared.Modules.Fittings.Commands;

// No [RequiresPermission] — push is a local ESI call gated only by the ESI scope check.
internal sealed class PushFittingToEsiCommandHandler(
    IFittingEsiClient esiClient,
    IFittingRepository repository) : ICommandHandler<PushFittingToEsiCommand, Result<int>>
{
    public async Task<Result<int>> Handle(PushFittingToEsiCommand command, CancellationToken cancellationToken = default)
    {
        // Find by DB id regardless of owner — fits are portable, you can push char A's fit with char B.
        var local = await repository.FindByIdAsync(command.LocalFittingId, cancellationToken);

        if (local is null)
            return Result<int>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, $"Fitting {command.LocalFittingId} not found.", "Fittings"));

        EsiFittingWrite write;
        try
        {
            var original = JsonSerializer.Deserialize<EsiFitting>(local.RawJson)
                           ?? throw new InvalidOperationException("Corrupt raw JSON in LocalFitting.");
            write = new EsiFittingWrite(original.Name, original.Description, original.ShipTypeId, original.Items);
        }
        catch (Exception ex)
        {
            return Result<int>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, $"Cannot deserialize fitting JSON: {ex.Message}", "Fittings"));
        }

        try
        {
            var newId = await esiClient.PostFittingAsync(command.CharacterId, command.AccessToken, write, cancellationToken);
            return Result<int>.Success(newId);
        }
        catch (Exception ex)
        {
            return Result<int>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.EsiFailed, ex.Message, "Fittings"));
        }
    }
}
