using EveUtils.Shared.Modules.Settings.Entities;

namespace EveUtils.Shared.Modules.Settings.Repositories;

public interface ISettingRepository
{
    Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default);
}
