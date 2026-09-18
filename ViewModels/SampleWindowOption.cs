using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiPing.ViewModels;

public sealed class SampleWindowOption
{
    public static IReadOnlyList<SampleWindowOption> Presets { get; } =
    [
        new(1, "60 seconds"),
        new(5, "5 min"),
        new(10, "10 min"),
        new(15, "15 min"),
        new(30, "30 min"),
        new(60, "60 min"),
        new(240, "4 hours"),
        new(480, "8 hours"),
        new(720, "12 hours"),
        new(1440, "24 hours"),
        new(2880, "48 hours")
    ];

    public SampleWindowOption(double minutes, string label)
    {
        Minutes = minutes;
        Label = label;
    }

    public double Minutes { get; }
    public string Label { get; }

    public static SampleWindowOption ForMinutes(double minutes) =>
        new(minutes, FormatMinutes(minutes));

    private static string FormatMinutes(double minutes)
    {
        if (Math.Abs(minutes - 1) < 0.0001)
            return "60 seconds";

        if (Math.Abs(minutes - 60) < 0.0001)
            return "60 min";

        if (minutes % 60 == 0)
        {
            double hours = minutes / 60;
            return $"{hours.ToString("0.##", CultureInfo.InvariantCulture)} {(hours == 1 ? "hour" : "hours")}";
        }

        return $"{minutes.ToString("0.##", CultureInfo.InvariantCulture)} min";
    }

    public override string ToString() => Label;
}
