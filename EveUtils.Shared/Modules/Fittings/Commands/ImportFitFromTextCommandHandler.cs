using System.Globalization;
using System.Text.Json;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fittings.Services.Parsers;

namespace EveUtils.Shared.Modules.Fittings.Commands;

// Local-only, no character/scope: parsing + storing a pasted fit needs neither ESI nor a permission.
internal sealed class ImportFitFromTextCommandHandler(
    IFitTextImporter importer, IFittingRepository repository)
    : ICommandHandler<ImportFitFromTextCommand, Result<string>>
{
    // Text imports aren't tied to a character — they live in an owner-agnostic local library. ESI-imported
    // fits use the character id as owner, so "0" is a private namespace for pasted fits and never collides.
    private const string LocalLibraryOwner = "0";

    public async Task<Result<string>> Handle(ImportFitFromTextCommand command, CancellationToken cancellationToken = default)
    {
        var parsed = importer.Import(command.Text);
        if (!parsed.Success)
            return Result<string>.Failure(
                new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed, parsed.Error!, "Fittings"));

        var fit = parsed.Fit!;
        var messages = parsed.Warnings
            .Select(w => new ResultMessage(MessageSeverity.Warning, MessageCodes.ValidationFailed, w, "Fittings"))
            .ToList();

        var rawJson = JsonSerializer.Serialize(fit);
        var contentHash = FitContentHash.Compute(rawJson);

        // Content-hash dedup (owner-agnostic, 2026-06-04): the same fit already in the library is reported, not stored.
        var duplicate = await repository.FindByContentHashAsync(contentHash, cancellationToken);
        if (duplicate is not null)
        {
            messages.Add(new ResultMessage(MessageSeverity.Info, MessageCodes.Duplicate,
                $"Skipped '{fit.Name}' — same fit already in your library as '{duplicate.Name}'.", "Fittings"));
            return Result<string>.Success(duplicate.Name, messages.ToArray());
        }

        await repository.UpsertAsync(new LocalFitting
        {
            OwnerId      = LocalLibraryOwner,
            EsiFittingId = StableId(contentHash), // content-derived id keeps the (owner, esiId) unique index distinct per fit
            Name         = fit.Name,
            ShipTypeId   = fit.ShipTypeId,
            RawJson      = rawJson,
            ContentHash  = contentHash,
            ImportedAt   = DateTimeOffset.UtcNow
        }, cancellationToken);

        return Result<string>.Success(fit.Name, messages.ToArray());
    }

    // 7 hex digits of the MD5 content hash → a stable, non-negative int (max 0x0FFFFFFF < int.MaxValue). The content
    // hash already guards true duplicates, so a distinct fit yields a distinct id within the local-library namespace.
    private static int StableId(string contentHash)
    {
        var slice = contentHash.Length >= 7 ? contentHash[..7] : contentHash.PadLeft(7, '0');
        return int.Parse(slice, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
