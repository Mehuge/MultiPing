using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>Traceroute mode: probes a single target with increasing TTL and tracks each hop.</summary>
public partial class PlotPingViewModel : MonitorViewModelBase
{
    [ObservableProperty] private string _target;

    // Maintain all rows (1-30) for data collection, even if not displayed
    private readonly Dictionary<int, ProbeRowViewModel> _allRowsByTtl = new();

    public PlotPingViewModel(AppConfig settings, ConfigService configSvc, PingService ping, TracerouteService trace, LogService log)
        : base(settings, configSvc, ping, trace, log)
    {
        _target = settings.PlotPingTarget;
    }

    public override AppMode Mode => AppMode.PlotPing;
    public override string WindowTitle => $"MultiPing — PlotPing (traceroute) : {Target}";

    protected override void OnStarting() => RememberHost(Target);

    protected override async Task RunRoundAsync(CancellationToken ct)
    {
        string target = Target.Trim();
        if (string.IsNullOrEmpty(target)) return;

        // Get ALL results (all 30 hops, not trimmed)
        var allHops = await Trace.RunRoundAsync(target, Settings.MaxHops, Settings.PingTimeoutMs, ct);

        // Ensure all 30 rows exist and add samples to ALL (for loss calculation)
        for (int ttl = 1; ttl <= Settings.MaxHops; ttl++)
        {
            ProbeRowViewModel row = GetOrCreateRow(ttl);

            var hop = allHops.FirstOrDefault(h => h.Ttl == ttl);
            if (hop.Ttl != 0) // HopResult was found
            {
                row.AddSample(new PingSample(DateTime.UtcNow, hop.RttMs));
            }
        }

        // Trim for display: rebuild Rows collection with only relevant hops
        var displayHops = Services.TracerouteService.TrimForDisplay(allHops);
        Rows.Clear();

        foreach (var hop in displayHops)
        {
            ProbeRowViewModel row = GetOrCreateRow(hop.Ttl);

            if (hop.Address is not null)
            {
                string ip = hop.Address.ToString();
                row.IpAddress = ip;
                row.DisplayLabel = $"{hop.Ttl}. {ip}";
            }
            else if (row.IpAddress == "*" || string.IsNullOrEmpty(row.IpAddress) || row.IpAddress == row.Host)
            {
                row.IpAddress = "*";
                row.DisplayLabel = $"{hop.Ttl}. *";
            }

            Rows.Add(row);
        }
    }

    private ProbeRowViewModel GetOrCreateRow(int ttl)
    {
        if (!_allRowsByTtl.TryGetValue(ttl, out var row))
        {
            row = new ProbeRowViewModel(ttl, host: "*") { DisplayLabel = $"{ttl}. *", IpAddress = "*" };
            _allRowsByTtl[ttl] = row;
        }
        return row;
    }

    public override void SaveSettings()
    {
        Settings.PlotPingTarget = Target;
        base.SaveSettings();
    }
}
