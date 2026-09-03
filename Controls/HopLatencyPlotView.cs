using System;
using System.Collections.Generic;
using Avalonia.Controls;
using MultiPing.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace MultiPing.Controls;

/// <summary>
/// A snapshot plot with probe index on the X axis and current/avg latency (ms) on the Y axis.
/// In PlotPing mode this is the classic traceroute profile; in MultiPing it shows the selected
/// destination's traceroute. The host calls <see cref="Update"/> after each round.
/// </summary>
public class HopLatencyPlotView : UserControl
{
    private readonly AvaPlot _plot = new();

    public HopLatencyPlotView()
    {
        Content = _plot;
        _plot.Plot.XLabel("Hop");
        _plot.Plot.YLabel("Latency (ms)");
        // Hops are integers — avoid fractional tick labels like "0.5".
        _plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { IntegerTicksOnly = true };

        // Transparent figure with a white data area so the plot stands out from its container.
        _plot.Plot.FigureBackground.Color = ScottPlot.Colors.Transparent;
        _plot.Plot.DataBackground.Color = ScottPlot.Colors.White;
    }

    public void Update(IReadOnlyList<ProbeRowViewModel> rows)
    {
        var plot = _plot.Plot;
        plot.Clear();

        int n = rows.Count;
        if (n > 0)
        {
            var xs = new double[n];
            var last = new double[n];
            var avg = new double[n];
            var mins = new double[n];
            var maxs = new double[n];
            double yMax = 1;

            for (int i = 0; i < n; i++)
            {
                var s = rows[i].Series.Snapshot();
                xs[i] = i + 1;
                last[i] = s.Last ?? double.NaN;
                avg[i] = s.Avg ?? double.NaN;
                mins[i] = s.Min ?? double.NaN;
                maxs[i] = s.Max ?? double.NaN;
                if (s.Max is double mx && mx > yMax) yMax = mx;
            }

            // Min–Max range as a light-green filled polygon (FillY doesn't render, so use Polygon instead).
            if (xs.Length > 0)
            {
                var points = new List<ScottPlot.Coordinates>();
                for (int i = 0; i < xs.Length; i++)
                {
                    if (!double.IsNaN(mins[i])) points.Add(new(xs[i], mins[i]));
                }
                for (int i = xs.Length - 1; i >= 0; i--)
                {
                    if (!double.IsNaN(maxs[i])) points.Add(new(xs[i], maxs[i]));
                }
                if (points.Count > 2)
                {
                    var poly = plot.Add.Polygon(points.ToArray());
                    poly.FillColor = new Color(144, 238, 144, 200);
                    poly.LineWidth = 0;
                }
            }

            // Average as a red/orange line.
            var avgLine = plot.Add.ScatterLine(xs, avg);
            avgLine.Color = new Color(255, 140, 0);
            avgLine.LineWidth = 2;
            avgLine.LegendText = "Avg";

            // Current as X markers only (no connecting line).
            var cur = plot.Add.ScatterPoints(xs, last);
            cur.MarkerShape = MarkerShape.Eks;
            cur.MarkerSize = 8;
            cur.Color = Colors.Black;
            cur.LegendText = "Current";

            // Red translucent vertical bands (hop-width) for dropouts (hops with no samples).
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(avg[i]) && double.IsNaN(last[i]) && double.IsNaN(mins[i]))
                {
                    var rect = plot.Add.Rectangle(xs[i] - 0.5, xs[i] + 0.5, 0, yMax * 1.15);
                    rect.FillColor = new Color(255, 0, 0, 100);
                    rect.LineWidth = 0;
                }
            }

            plot.Title("Current Traceroute");
            plot.Axes.SetLimits(0.5, n + 0.5, 0, yMax * 1.15);
            plot.Legend.IsVisible = false;
        }

        _plot.Refresh();
    }
}
