using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Settings.Commands;

public sealed record DeleteSettingCommand(string Key) : ICommand<Result>;
