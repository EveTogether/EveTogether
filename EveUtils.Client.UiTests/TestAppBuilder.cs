using Avalonia;
using Avalonia.Headless;
using EveUtils.Client;
using EveUtils.Client.UiTests;

// Registers the Avalonia application used for every headless UI test in this assembly.
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Run tests serially: TestClientInstance isolates via the process-global EVEUTILS_INSTANCE env var, so two instances
// built in parallel would race on it (same DB → "table already exists" during migrate). Headless Avalonia tests also
// want a single UI thread. Serial execution keeps both correct.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace EveUtils.Client.UiTests;

/// <summary>
/// Boots the real client <see cref="App"/> (for its Fluent theme + resources) on the in-process headless
/// windowing backend, with Skia software-rendering enabled so <c>CaptureRenderedFrame()</c> yields real pixels.
/// The desktop-lifetime branch in <see cref="App.OnFrameworkInitializationCompleted"/> is skipped under headless,
/// so no real window opens and <c>Program.Services</c> is never touched.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            // The face the real client starts in (Program.cs). Without it every headless capture came out in the
            // platform's default font, so anything read off a rendered frame — spacing, optical alignment, whether a
            // figure still fits its column — was being judged in a typeface no user ever sees. Found in ET-110: an
            // alignment the operator could see on his build measured as perfect here, and AppraisalToolTests was
            // green over a column that really does clip its amount.
            .WithInterFont()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
}
