using System.Runtime.CompilerServices;
using EveUtils.Client.Formatting;

namespace EveUtils.Client.UiTests;

internal static class TestCulture
{
    // Runs before the first test. The suite asserts on the app's readouts, so it has to run under the same
    // culture the app pins at start-up — otherwise the expected strings only hold on an en-US machine and
    // every readout test turns red on a comma-decimal locale instead of catching a real regression.
    [ModuleInitializer]
    internal static void Apply() => ClientCulture.Apply();
}
