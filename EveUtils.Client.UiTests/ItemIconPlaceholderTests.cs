using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.Killmails;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-496: an item with no icon of its own shows the one bundled placeholder, never a letter tile.</summary>
public sealed class ItemIconPlaceholderTests
{
    private const int BlueprintTypeId = 85957;

    [AvaloniaFact]
    public async Task LootLine_NoIconAvailable_ShowsPlaceholder()
    {
        var line = new ActivityLootLineViewModel(BlueprintTypeId, "Glorified Decayed 50MN Microwarpdrive Mutaplasmid Blueprint",
            1, null, LootKind.Gained);

        await line.LoadIconAsync(new _ImagesWithout());

        Assert.Same(TypeImagePlaceholder.Bitmap, line.Icon);
    }

    [AvaloniaFact]
    public async Task LootLine_IconAvailable_KeepsItsOwnIcon()
    {
        var own = new WriteableBitmap(new Avalonia.PixelSize(2, 2), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        var line = new ActivityLootLineViewModel(34, "Tritanium", 1, null, LootKind.Gained);

        await line.LoadIconAsync(new _ImagesWithout(own));

        Assert.Same(own, line.Icon);
    }

    [AvaloniaFact]
    public async Task KillmailItemRow_NoIconAvailable_ShowsPlaceholder()
    {
        var row = new KillmailDetailItemRowViewModel(
            [new KillmailDetailItemLineDto(Flag: 5, BlueprintTypeId, IsNested: false, IsDestroyed: false, Quantity: 1, Value: null)],
            "Some Blueprint", null);

        await row.LoadIconAsync(new _ImagesWithout());

        Assert.Same(TypeImagePlaceholder.Bitmap, row.Icon);
    }

    private sealed class _ImagesWithout(Bitmap? image = null) : ITypeImageProvider
    {
        public Task<bool> AreImagesEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<Bitmap?> GetImageAsync(int typeId, TypeImageKind kind, int size,
            CancellationToken cancellationToken = default) => Task.FromResult(image);
    }
}
