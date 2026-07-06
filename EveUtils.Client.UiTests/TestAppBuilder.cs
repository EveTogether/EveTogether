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
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
}
