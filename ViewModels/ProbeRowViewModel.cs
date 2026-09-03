using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>
/// A single row in the top-left grid — either a traceroute hop or a MultiPing destination.
/// Wraps a <see cref="SampleSeries"/> and exposes formatted statistics plus a per-row plot toggle.
/// </summary>
public partial class ProbeRowViewModel : ObservableObject
{
    /// <summary>Raised on the UI thread after this row's series receives new data, so plots can redraw.</summary>
    public event Action? SeriesUpdated;

    public ProbeRowViewModel(int index, string host)
    {
        Index = index;
        Host = host;
        _ipAddress = host;
        _displayLabel = host;
    }

    /// <summary>Hop number (traceroute) or destination row number (MultiPing).</summary>
    public int Index { get; }

    /// <summary>The configured target host (MultiPing). For a hop this equals the responding IP.</summary>
    public string Host { get; }

    /// <summary>Rolling sample buffer feeding both the statistics and the time-series plot.</summary>
    public SampleSeries Series { get; } = new();

    [ObservableProperty] private string _ipAddress;
    [ObservableProperty] private string _displayLabel;
    [ObservableProperty] private bool _plotEnabled;

    [ObservableProperty] private string _rttText = "-";
    [ObservableProperty] private string _minText = "-";
    [ObservableProperty] private string _maxText = "-";
    [ObservableProperty] private string _avgText = "-";
    [ObservableProperty] private string _plText = "0.0";

    /// <summary>Toggles whether this row's time-series plot is shown along the bottom.</summary>
    [RelayCommand]
    private void TogglePlot() => PlotEnabled = !PlotEnabled;

    /// <summary>Adds a sample, recomputes the displayed statistics, and notifies subscribers.</summary>
    public void AddSample(PingSample sample)
    {
        Series.Add(sample);
        RefreshStats();
        SeriesUpdated?.Invoke();
    }

    public void RefreshStats()
    {
        SeriesStatistics s = Series.Snapshot();
        RttText = Num(s.Last);
        MinText = Num(s.Min);
        MaxText = Num(s.Max);
        AvgText = Num(s.Avg);
        PlText = s.PacketLossPercent.ToString("0", CultureInfo.InvariantCulture);
    }

    public LogEntry ToLogEntry() => new(Index, IpAddress, Series.Snapshot());

    private static string Num(double? v) =>
        v is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "-";
}
