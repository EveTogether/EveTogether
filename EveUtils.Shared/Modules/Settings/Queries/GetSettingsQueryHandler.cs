using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Shared.Modules.Settings.Queries;

internal sealed class GetSettingsQueryHandler(ISettingRepository repository)
    : IQueryHandler<GetSettingsQuery, IReadOnlyList<SettingDto>>
{
    public async Task<IReadOnlyList<SettingDto>> Handle(GetSettingsQuery query, CancellationToken cancellationToken = default)
    {
        var settings = await repository.ListAsync(cancellationToken);
        return settings.Select(s => new SettingDto(s.Id, s.Key, s.Value)).ToList();
    }
}
