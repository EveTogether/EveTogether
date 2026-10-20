using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.Runs;

/// <param name="PilotCount">The pilots behind a shared figure, the server's own count; the character count for the own runs.</param>
/// <param name="ServerName">The server a shared figure came from, null for the own runs.</param>
/// <param name="ServerAddress">Its address, null for the own runs.</param>
/// <param name="ReadAtUtc">When the figures were read, for the panel's "updated … ago"; null where the reader did not say.</param>
public sealed record ExpectedEarnings(EarningsBasis Basis, EarningsFiguresDto Figures, int PilotCount, string? ServerName = null,
    string? ServerAddress = null, DateTime? ReadAtUtc = null);
