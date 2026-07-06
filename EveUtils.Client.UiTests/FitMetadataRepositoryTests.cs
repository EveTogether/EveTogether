using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Fit-metadata: a local fit carries a user-editable name, description and tags. Editing them touches none of
/// the modules, so the content fingerprint — the fit's identity — stays the same: a renamed/tagged fit is still
/// the same fit.
/// </summary>
public class FitMetadataRepositoryTests
{
    [AvaloniaFact]
    public async Task UpdateMetadata_ChangesNameDescriptionTags_KeepingIdentity()
    {
        using var instance = TestClientInstance.Create();
        var repo = instance.Services.GetRequiredService<IFittingRepository>();
        await repo.UpsertAsync(new LocalFitting
        {
            OwnerId = "95600001", EsiFittingId = 7001, Name = "Hawk — Rockets", ShipTypeId = 11993,
            RawJson = "{\"ship_type_id\":11993,\"items\":[]}", ImportedAt = DateTimeOffset.UtcNow
        });
        var seeded = (await repo.ListByOwnerAsync("95600001")).Single();
        var hashBefore = seeded.ContentHash;

        await repo.UpdateMetadataAsync(seeded.Id, "Hawk — PvP", "Cheap brawler", "pvp, cheap");

        var after = await repo.FindByIdAsync(seeded.Id);
        Assert.Equal("Hawk — PvP", after!.Name);
        Assert.Equal("Cheap brawler", after.Description);
        Assert.Equal("pvp, cheap", after.Tags);
        Assert.Equal(hashBefore, after.ContentHash);   // metadata edit never changes identity
    }
}
