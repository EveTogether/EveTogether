using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Settings.Dtos;

namespace EveUtils.Shared.Modules.Settings.Queries;

public sealed record GetSettingsQuery : IQuery<IReadOnlyList<SettingDto>>;
