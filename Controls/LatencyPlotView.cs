using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MultiPing.ViewModels;
using ScottPlot.Avalonia;

namespace MultiPing.Controls;

public class LatencyPlotView : UserControl
{
    public static readonly StyledProperty<ProbeRowViewModel?> RowProperty =
        AvaloniaProperty.Register<LatencyPlotView, ProbeRowViewModel?>(nameof(Row));

    public static readonly StyledProperty<double> WindowMinutesProperty =
        AvaloniaProperty.Register<LatencyPlotView, double>(nameof(WindowMinutes), 30);

    public static readonly StyledProperty<double> OffsetMinutesProperty =
        AvaloniaProperty.Register<LatencyPlotView, double>(nameof(OffsetMinutes), 0);

    public static readonly StyledProperty<int> PingIntervalMsProperty =
        AvaloniaProperty.Register<LatencyPlotView, int>(nameof(PingIntervalMs), 1000);

    private readonly AvaPlot _plot = new();
    private readonly TextBlock _title = new() { FontSize = 10, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 0, 0, 0) };
    private ProbeRowViewModel? _subscribed;

    // Render Throttle Flags
    private bool _isRenderScheduled;

    public LatencyPlotView()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = Brushes.Transparent;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(_title, 0);
        Grid.SetRow(_plot, 1);
        grid.Children.Add(_title);
        grid.Children.Add(_plot);
        Content = grid;

        var plot = _plot.Plot;

        plot.Axes.DateTimeTicksBottom();
        if (plot.Axes.Bottom.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic dt)
            dt.LabelFormatter = d => d.ToString("HH:mm:ss");

        plot.Axes.Bottom.TickLabelStyle.FontSize = 9;
        plot.Axes.Left.TickLabelStyle.FontSize = 9;

        plot.Axes.Margins(bottom: 0, top: 0, left: 0, right: 0);
        plot.Axes.Top.MinimumSize = 0;
        plot.Axes.Top.MaximumSize = 6;

        plot.FigureBackground.Color = ScottPlot.Colors.Transparent;
        plot.DataBackground.Color = ScottPlot.Colors.White;
        plot.Legend.IsVisible = false;
    }

    public ProbeRowViewModel? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public double WindowMinutes
    {
        get => GetValue(WindowMinutesProperty);
        set => SetValue(WindowMinutesProperty, value);
    }

    public double OffsetMinutes
    {
        get => GetValue(OffsetMinutesProperty);
        set => SetValue(OffsetMinutesProperty, value);
    }

    public int PingIntervalMs
    {
        get => GetValue(PingIntervalMsProperty);
        set => SetValue(PingIntervalMsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RowProperty)
        {
            if (_subscribed is not null) _subscribed.SeriesUpdated -= Refresh;
            _subscribed = change.GetNewValue<ProbeRowViewModel?>();
            if (_subscribed is not null) _subscribed.SeriesUpdated += Refresh;
            Refresh();
        }
        else if (change.Property == WindowMinutesProperty || change.Property == OffsetMinutesProperty || change.Property == PingIntervalMsProperty)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Throttles and queues refresh operations to avoid mid-render state collisions.
    /// </summary>
    private void Refresh()
    {
        // If a frame render is already queued for the UI thread, skip creating another dispatch.
        if (_isRenderScheduled) return;

        _isRenderScheduled = true;

        // Post execution to Avalonia's UI thread at Render priority
        Dispatcher.UIThread.InvokeAsync(RenderInternal, DispatcherPriority.Render);
    }

    private void RenderInternal()
    {
        _isRenderScheduled = false;

        var plot = _plot.Plot;
        plot.Clear();

        if (Row is { } row)
        {
            _title.Text = row.DisplayLabel;

            DateTime endUtc = DateTime.UtcNow.AddMinutes(-OffsetMinutes);
            DateTime startUtc = endUtc.AddMinutes(-WindowMinutes);

            row.Series.GetWindow(startUtc, endUtc, out double[] xs, out double[] ys);

            double yMax = 1;
            var dropoutTimes = new List<double>();
            if (xs.Length > 0)
            {
                var scatter = plot.Add.Scatter(xs, ys);
                scatter.MarkerSize = 3;

                for (int i = 0; i < ys.Length; i++)
                {
                    if (double.IsNaN(ys[i]))
                        dropoutTimes.Add(xs[i]);
                    else if (ys[i] > yMax)
                        yMax = ys[i];
                }

                double barWidth = PingIntervalMs / 1000.0 / 86400;
                foreach (double xDropout in dropoutTimes)
                {
                    var rect = plot.Add.Rectangle(xDropout - barWidth / 2, xDropout + barWidth / 2, 0, yMax * 1.15);
                    rect.FillColor = new ScottPlot.Color(255, 0, 0, 80);
                    rect.LineWidth = 0;
                }
            }

            plot.Axes.SetLimits(startUtc.ToOADate(), endUtc.ToOADate(), 0, yMax * 1.15);
        }
        else
        {
            _title.Text = string.Empty;
        }

        _plot.Refresh();
    }
}