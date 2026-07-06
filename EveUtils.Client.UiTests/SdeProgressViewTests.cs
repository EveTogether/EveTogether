using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Sde.Import;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The SDE update popup: the importer reports into <see cref="SdeProgressViewModel"/>, which drives a
/// 0-100% download bar, then "x / y processed", and signals close on a successful finish. Drives the real Report
/// path, asserts the derived state, renders the window headless, and checks the auto-close signal.
/// </summary>
public class SdeProgressViewTests
{
    private const long Mb = 1_048_576;

    [AvaloniaFact]
    public void SdeProgress_DownloadThenProcess_RendersAndAutoCloses()
    {
        var vm = new SdeProgressViewModel();

        // Download phase: 40 / 80 MB -> a determinate 50% bar.
        vm.Report(new SdeImportProgress(SdeImportPhase.Downloading, 40 * Mb, 80 * Mb));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsIndeterminate);
        Assert.Equal(50d, vm.ProgressPercent, 1);

        // Processing phase: switches to "x / y processed".
        vm.Report(new SdeImportProgress(SdeImportPhase.Processing, ProcessedItems: 44_000, TotalItems: 86_457));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsIndeterminate);
        Assert.InRange(vm.ProgressPercent, 50d, 52d);
        Assert.Contains("processed", vm.DetailText);
        Assert.False(vm.IsError);

        var window = new SdeProgressWindow(vm);
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-sde-progress.png");
        window.Close();

        // A successful finish asks the popup to close itself.
        var closeRequested = false;
        vm.CloseRequested += () => closeRequested = true;
        vm.Report(new SdeImportProgress(SdeImportPhase.Completed));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsFinished);
        Assert.True(closeRequested);
    }

    [AvaloniaFact]
    public void SdeProgress_Failure_StaysOpenWithError()
    {
        var vm = new SdeProgressViewModel();
        var closeRequested = false;
        vm.CloseRequested += () => closeRequested = true;

        vm.Report(new SdeImportProgress(SdeImportPhase.Failed, Error: "network unreachable"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsError);
        Assert.True(vm.IsFinished);
        Assert.Contains("network unreachable", vm.DetailText);
        Assert.False(closeRequested); // failure does not auto-close; the user dismisses it

        // The Close command is what dismisses an errored popup.
        vm.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(closeRequested);
    }
}
