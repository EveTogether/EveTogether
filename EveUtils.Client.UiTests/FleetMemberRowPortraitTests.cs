using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Stream B / B-3: a fleet member leaf loads the pilot's ESI hex portrait best-effort. A real character resolves a
/// portrait (image shown over the glyph); an external/unknown member (character id ≤ 0) is skipped so it keeps the
/// initial-glyph fallback and we never make a pointless image call.
/// </summary>
public class FleetMemberRowPortraitTests
{
    private sealed class FakePortraits(Bitmap? result) : ICharacterPortraitProvider
    {
        public int LastCharacterId = -1;
        public Task<Bitmap?> GetPortraitAsync(int characterId, int size, CancellationToken cancellationToken = default)
        {
            LastCharacterId = characterId;
            return Task.FromResult(result);
        }
    }

    private static FleetMemberRowViewModel Row(int characterId) => new(
        memberId: 1, characterId: characterId, characterName: "Lionear", roleLabel: "DPS",
        assignedFit: null, skillBadge: null, selectFitCommand: new AsyncRelayCommand(() => Task.CompletedTask));

    [AvaloniaFact]
    public async Task LoadPortrait_SetsPortrait_ForRealCharacter()
    {
        var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var row = Row(90250177);
        var portraits = new FakePortraits(bitmap);

        await row.LoadPortraitAsync(portraits, TestContext.Current.CancellationToken);

        Assert.True(row.HasPortrait);
        Assert.Equal(90250177, portraits.LastCharacterId);
    }

    [Fact]
    public async Task LoadPortrait_SkipsExternalMember_AndKeepsGlyphFallback()
    {
        var portraits = new FakePortraits(null);
        var row = Row(0);   // external / unknown → no ESI portrait, falls back to the initial glyph

        await row.LoadPortraitAsync(portraits, TestContext.Current.CancellationToken);

        Assert.False(row.HasPortrait);
        Assert.Equal("L", row.Initial);
        Assert.Equal(-1, portraits.LastCharacterId);   // provider was never called for a non-resolvable member
    }
}
