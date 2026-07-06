using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Settings.Commands;

public sealed record SetSettingCommand(string Key, string Value) : ICommand<Result>;
