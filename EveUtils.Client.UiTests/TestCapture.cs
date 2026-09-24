using Avalonia.Media.Imaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Saves a rendered frame under the run's own scratch root (<see cref="TestDataRoot"/>, ET-359) instead of the
/// shared temp root, so screenshots are removed with the rest of the run's data instead of piling up in
/// %TEMP% forever. Also asserts the frame is not null, so call sites no longer each need their own `!`.
/// </summary>
internal static class TestCapture
{
    internal static void Save(Bitmap? frame, string fileName)
    {
        Assert.NotNull(frame);
        frame!.Save(System.IO.Path.Combine(TestDataRoot.Path, fileName), new PngBitmapEncoderOptions());
    }
}
