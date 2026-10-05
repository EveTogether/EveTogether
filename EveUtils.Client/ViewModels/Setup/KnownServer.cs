using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.Setup;

/// <summary>A server this PC is already coupled to, with the characters coupled to it.</summary>
public sealed record KnownServer(string Address, string DisplayName, IReadOnlyList<string> CoupledCharacterNames);
