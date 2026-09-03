using System;
using System.Diagnostics;
using MultiPing.Models;

namespace MultiPing.Services;

/// <summary>Launches additional app instances as separate OS processes, one window per process.</summary>
public static class WindowLauncher
{
    public static void LaunchNew(AppMode mode)
    {
        string arg = mode == AppMode.PlotPing ? "plotping" : "multiping";
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--mode");
            psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch
        {
            // If spawning fails (e.g. exe path unavailable during dev), fail silently.
        }
    }
}
