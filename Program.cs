using System;
using Avalonia;
using MultiPing.Models;

namespace MultiPing;

internal static class Program
{
    /// <summary>Mode this process runs in, parsed from the command line before Avalonia starts.</summary>
    public static AppMode StartupMode { get; private set; } = AppMode.PlotPing;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        StartupMode = ParseMode(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppMode ParseMode(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase)) continue;
            return args[i + 1].ToLowerInvariant() switch
            {
                "multiping" => AppMode.MultiPing,
                "plotping" => AppMode.PlotPing,
                _ => AppMode.PlotPing,
            };
        }
        return AppMode.PlotPing;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
