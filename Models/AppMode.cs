namespace MultiPing.Models;

/// <summary>The two operating modes of the application. Each app process runs in exactly one mode.</summary>
public enum AppMode
{
    /// <summary>Single-target traceroute view with per-hop statistics and plots.</summary>
    PlotPing,

    /// <summary>Multiple-destination view where each configured IP is monitored like a hop.</summary>
    MultiPing,
}
