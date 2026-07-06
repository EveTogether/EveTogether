using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>
/// Imports a pasted community fit (EFT or DNA, auto-detected) into the local library. The handler parses +
/// SDE-resolves the text, dedups by content hash, and stores it. The success value is the stored (or matched) fit
/// name; parser warnings + the duplicate notice travel as result messages.
/// </summary>
public sealed record ImportFitFromTextCommand(string Text) : ICommand<Result<string>>;
