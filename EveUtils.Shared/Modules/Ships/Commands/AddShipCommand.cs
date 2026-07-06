using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Ships.Commands;

public sealed record AddShipCommand(string Name, string Class, decimal Mass) : ICommand<Result<int>>;
