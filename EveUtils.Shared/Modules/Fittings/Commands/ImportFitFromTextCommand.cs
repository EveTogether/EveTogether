using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>
/// Imports a pasted community fit into the local library: EFT, DNA, an eveship.fit link, or an EVE Workbench
/// fit link (fetched as EFT over their public API first). The handler parses + SDE-resolves the text, dedups by
/// content hash and stores it; the success value is the stored (or matched) fit name.
/// </summary>
public sealed record ImportFitFromTextCommand(string Text) : ICommand<Result<string>>;
