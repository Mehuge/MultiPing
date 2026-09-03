using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>
/// Shared plumbing for both modes: the probe loop, start/stop control, the sample-window / scroll state,
/// logging integration, and settings persistence. Derived classes implement a single probe round.
/// </summary>
public abstract partial class MonitorViewModelBase : ObservableObject
{
    protected readonly ConfigService ConfigSvc;
    protected readonly PingService Ping;
    protected readonly TracerouteService Trace;
    protected readonly LogService Log;
    protected readonly AppConfig Settings;

    private CancellationTokenSource? _cts;

    /// <summary>Raised on the UI thread after each completed probe round so views can refresh singleton plots.</summary>
    public event Action? RoundCompleted;

    protected MonitorViewModelBase(AppConfig settings, ConfigService configSvc, PingService ping, TracerouteService trace, LogService log)
    {
        Settings = settings;
        ConfigSvc = configSvc;
        Ping = ping;
        Trace = trace;
        Log = log;

        _sampleWindowMinutes = settings.SampleWindowMinutes;
        _loggingEnabled = settings.LogByDefault;
        _logByDefault = settings.LogByDefault;
        _pingIntervalMs = settings.PingIntervalMs;

        Rows.CollectionChanged += OnRowsCollectionChanged;
    }

    public abstract AppMode Mode { get; }
    public abstract string WindowTitle { get; }

    public ObservableCollection<ProbeRowViewModel> Rows { get; } = new();

    /// <summary>
    /// The subset of <see cref="Rows"/> whose plots are enabled, in row order. The bottom panel binds
    /// to this so it divides its height evenly among only the visible plots.
    /// </summary>
    public ObservableCollection<ProbeRowViewModel> PlottedRows { get; } = new();

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ProbeRowViewModel r in e.OldItems)
            {
                r.PropertyChanged -= OnRowPropertyChanged;
                PlottedRows.Remove(r);
            }

        if (e.NewItems is not null)
            foreach (ProbeRowViewModel r in e.NewItems)
            {
                r.PropertyChanged += OnRowPropertyChanged;
                if (r.PlotEnabled) InsertPlotted(r);
            }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProbeRowViewModel.PlotEnabled) || sender is not ProbeRowViewModel r)
            return;

        if (r.PlotEnabled)
        {
            if (!PlottedRows.Contains(r)) InsertPlotted(r);
        }
        else
        {
            PlottedRows.Remove(r);
        }
    }

    /// <summary>Inserts a row into <see cref="PlottedRows"/> so it keeps the same order as <see cref="Rows"/>.</summary>
    private void InsertPlotted(ProbeRowViewModel r)
    {
        int idx = 0;
        foreach (var row in Rows)
        {
            if (ReferenceEquals(row, r)) break;
            if (row.PlotEnabled && PlottedRows.Contains(row)) idx++;
        }
        PlottedRows.Insert(Math.Min(idx, PlottedRows.Count), r);
    }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _sampleWindowMinutes;
    [ObservableProperty] private double _scrollOffsetMinutes;
    [ObservableProperty] private bool _loggingEnabled;
    [ObservableProperty] private bool _logByDefault;
    [ObservableProperty] private int _pingIntervalMs;

    /// <summary>Ping interval in seconds (for UI display). Syncs with PingIntervalMs.</summary>
    public int PingIntervalSeconds
    {
        get => PingIntervalMs / 1000;
        set
        {
            int ms = value * 1000;
            if (PingIntervalMs != ms) PingIntervalMs = ms;
        }
    }
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private ProbeRowViewModel? _selectedRow;

    /// <summary>Label for the single run toggle button.</summary>
    public string RunButtonText => IsRunning ? "Stop" : "Start";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(RunButtonText));

    partial void OnLoggingEnabledChanged(bool value)
    {
        // Logging only writes while a trace is running; sync the file state to the toggle.
        if (value && IsRunning) OpenLog();
        else if (!value) Log.Close();
    }

    partial void OnLogByDefaultChanged(bool value)
    {
        // App-wide default is persisted immediately so all windows pick it up.
        Settings.LogByDefault = value;
        ConfigSvc.Save(Settings);
    }

    partial void OnPingIntervalMsChanged(int value)
    {
        Settings.PingIntervalMs = value;
        ConfigSvc.Save(Settings);
    }

    [RelayCommand]
    private void ToggleRun()
    {
        if (IsRunning) Stop();
        else Start();
    }

    [RelayCommand]
    private void StartRun() => Start();

    [RelayCommand]
    private void StopRun() => Stop();

    public void Start()
    {
        if (IsRunning) return;
        OnStarting();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        StatusText = "Running";
        if (LoggingEnabled) OpenLog();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        IsRunning = false;
        StatusText = "Stopped";
        Log.Close();
    }

    /// <summary>Hook for derived classes to (re)build their rows before a run begins.</summary>
    protected virtual void OnStarting() { }

    /// <summary>Performs one probe round, updating rows/series. Must run on the UI thread.</summary>
    protected abstract Task RunRoundAsync(CancellationToken ct);

    private async Task RunLoopAsync(CancellationToken ct)
    {
        DateTime nextFireTime = DateTime.UtcNow.AddMilliseconds(Settings.PingIntervalMs);
        CancellationTokenSource? currentRoundCts = null;

        while (!ct.IsCancellationRequested)
        {
            // Wait until next scheduled fire time
            long delayMs = (long)(nextFireTime - DateTime.UtcNow).TotalMilliseconds;
            if (delayMs > 0)
            {
                try
                {
                    await Task.Delay((int)delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (ct.IsCancellationRequested) break;

            // Fire time has arrived: cancel any in-flight round and start new one
            currentRoundCts?.Cancel();
            currentRoundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                await RunRoundAsync(currentRoundCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Round was aborted (either by next interval or overall stop); timeouts already marked
            }
            catch (Exception ex)
            {
                StatusText = "Error: " + ex.Message;
            }
            finally
            {
                currentRoundCts?.Dispose();
                currentRoundCts = null;
            }

            if (Log.IsOpen)
                Log.WriteRound(DateTime.Now, Rows.Select(r => r.ToLogEntry()));

            RoundCompleted?.Invoke();

            // Schedule next round at fixed wall-clock interval
            nextFireTime = nextFireTime.AddMilliseconds(Settings.PingIntervalMs);
        }
    }

    private void OpenLog()
    {
        string dir = string.IsNullOrWhiteSpace(Settings.LogDirectory)
            ? ConfigService.DefaultLogDirectory
            : Settings.LogDirectory;
        Log.Open(Mode, dir, DateTime.Now);
        StatusText = Log.CurrentPath is { } p ? "Logging to " + p : StatusText;
    }

    // --- Scrolling of the time-series plots -------------------------------------------------

    [RelayCommand]
    private void ScrollLeft() => ScrollOffsetMinutes += SampleWindowMinutes / 4.0;

    [RelayCommand]
    private void ScrollRight() =>
        ScrollOffsetMinutes = Math.Max(0, ScrollOffsetMinutes - SampleWindowMinutes / 4.0);

    [RelayCommand]
    private void ScrollLive() => ScrollOffsetMinutes = 0;

    // --- Menu commands ----------------------------------------------------------------------

    [RelayCommand]
    private void NewPlotPingWindow() => WindowLauncher.LaunchNew(AppMode.PlotPing);

    [RelayCommand]
    private void NewMultiPingWindow() => WindowLauncher.LaunchNew(AppMode.MultiPing);

    [RelayCommand]
    private void OpenLogFolder()
    {
        string dir = string.IsNullOrWhiteSpace(Settings.LogDirectory)
            ? ConfigService.DefaultLogDirectory
            : Settings.LogDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    /// <summary>Copies current UI state into settings and persists them. Called on window close.</summary>
    public virtual void SaveSettings()
    {
        Settings.SampleWindowMinutes = SampleWindowMinutes;
        ConfigSvc.Save(Settings);
    }
}
