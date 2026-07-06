using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// Result of comparing the local store against CCP's latest manifest. <see cref="UpdateAvailable"/> is true when
/// there is no local store yet or the remote build is newer — the client uses it to decide whether to ask the user.
/// </summary>
public sealed record SdeUpdateCheck(bool UpdateAvailable, SdeVersion? Local, SdeVersion Remote);
