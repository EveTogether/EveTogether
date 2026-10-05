using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EveUtils.Client.Controls.Map;

/// <param name="Size">The text itself; the image is <see cref="MapLabelCache.Inset"/> larger on every side.</param>
internal sealed record MapLabel(RenderTargetBitmap Image, Size Size)
{
    public void Draw(DrawingContext context, Point origin) => context.DrawImage(Image,
        new Rect(origin.X - MapLabelCache.Inset, origin.Y - MapLabelCache.Inset,
            Size.Width + MapLabelCache.Inset * 2, Size.Height + MapLabelCache.Inset * 2));
}
