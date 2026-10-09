using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EveUtils.Client.Imaging;

/// <summary>The one bundled image shown for a type the image server has no icon for (SKINs, blueprints, new items).</summary>
public static class TypeImagePlaceholder
{
    private static readonly Lazy<Bitmap> _bitmap =
        new(() => new Bitmap(AssetLoader.Open(new Uri("avares://EveUtils.Client/Assets/skin-placeholder.png"))));

    public static Bitmap Bitmap => _bitmap.Value;
}
