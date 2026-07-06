using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Sde.Import;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Drives the SDE update popup. It is the <see cref="IProgress{T}"/> sink handed to the importer: the download
/// phase shows a 0-100% bar, then it switches to "x / y processed". Terminal phases raise
/// <see cref="CloseRequested"/> so the window closes itself on success/already-up-to-date; on failure it stays
/// open showing the error with a Close button. Reports marshal onto the UI thread.
/// </summary>
public partial class SdeProgressViewModel : ViewModelBase, IProgress<SdeImportProgress>
{
    [ObservableProperty] private string _statusText = "Checking for updates…";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isFinished;

    /// <summary>Raised when the popup should close (success, already-up-to-date, or the user dismisses an error).</summary>
    public event Action? CloseRequested;

    public void Report(SdeImportProgress value) => Dispatcher.UIThread.Post(() => Apply(value));

    private void Apply(SdeImportProgress p)
    {
        switch (p.Phase)
        {
            case SdeImportPhase.CheckingVersion:
                StatusText = "Checking for updates…";
                DetailText = string.Empty;
                IsIndeterminate = true;
                break;
            case SdeImportPhase.Downloading:
                StatusText = "Downloading EVE static data…";
                if (p.TotalBytes > 0)
                {
                    IsIndeterminate = false;
                    ProgressPercent = p.DownloadFraction * 100;
                    DetailText = $"{Mb(p.DownloadedBytes)} / {Mb(p.TotalBytes)} MB";
                }
                else
                {
                    IsIndeterminate = true;
                    DetailText = $"{Mb(p.DownloadedBytes)} MB";
                }
                break;
            case SdeImportPhase.Preparing:
                StatusText = "Preparing…";
                DetailText = string.Empty;
                IsIndeterminate = true;
                break;
            case SdeImportPhase.Processing:
                StatusText = "Processing data…";
                IsIndeterminate = false;
                ProgressPercent = p.ProcessFraction * 100;
                DetailText = $"{p.ProcessedItems:n0} / {p.TotalItems:n0} processed";
                break;
            case SdeImportPhase.Finalizing:
                StatusText = "Finalizing…";
                DetailText = string.Empty;
                IsIndeterminate = true;
                ProgressPercent = 100;
                break;
            case SdeImportPhase.Completed:
                StatusText = "Up to date";
                ProgressPercent = 100;
                IsIndeterminate = false;
                IsFinished = true;
                CloseRequested?.Invoke();
                break;
            case SdeImportPhase.AlreadyUpToDate:
                IsFinished = true;
                CloseRequested?.Invoke();
                break;
            case SdeImportPhase.Failed:
                StatusText = "Update failed";
                DetailText = p.Error ?? "Unknown error";
                IsError = true;
                IsIndeterminate = false;
                IsFinished = true;
                break;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private static long Mb(long bytes) => bytes / 1_048_576;
}
