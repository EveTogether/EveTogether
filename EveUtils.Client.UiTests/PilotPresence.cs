using EveUtils.Client.Platform;

namespace EveUtils.Client.UiTests;

/// <summary>Which of your own characters have an EVE client up: the ones in <paramref name="mine"/> answer by
/// <paramref name="online"/>, everyone else is not ours and gets no verdict.</summary>
internal sealed class PilotPresence(int[] mine, int[] online) : ILocalCharacterPresence
{
    public bool? IsInGame(int characterId, string? characterName) => mine.Contains(characterId) ? online.Contains(characterId) : null;

    public bool? IsInGame(int characterId) => IsInGame(characterId, null);

    public IDisposable Subscribe(Action handler) => new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
