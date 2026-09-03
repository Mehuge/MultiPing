using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiPing.Models;
using MultiPing.Services;

namespace MultiPing.ViewModels;

/// <summary>
/// MultiPing mode: each configured destination is pinged directly (like a hop) every round.
/// The top-right panel shows a live traceroute of the currently selected destination.
/// </summary>
public partial class MultiPingViewModel : MonitorViewModelBase
{
    private const int DirectPingTtl = 128;
    private const int SelectedTraceEveryRounds = 5;

    private int _roundCounter;
    private int _nextIndex = 1;

    [ObservableProperty] private string _newTargetInput = string.Empty;

    /// <summary>Hops of the traceroute to the currently selected destination (top-right panel).</summary>
    public ObservableCollection<ProbeRowViewModel> SelectedTraceHops { get; } = new();

    public MultiPingViewModel(AppConfig settings, ConfigService configSvc, PingService ping, TracerouteService trace, LogService log)
        : base(settings, configSvc, ping, trace, log)
    {
        foreach (string host in settings.MultiPingTargets)
            Rows.Add(CreateRow(host));
    }

    public override AppMode Mode => AppMode.MultiPing;
    public override string WindowTitle => "MultiPing — Multiple destinations";

    private ProbeRowViewModel CreateRow(string host) =>
        new(_nextIndex++, host) { DisplayLabel = host, IpAddress = host };

    [RelayCommand]
    private void AddTarget()
    {
        string host = NewTargetInput.Trim();
        if (host.Length == 0) return;
        Rows.Add(CreateRow(host));
        NewTargetInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedRow is { } row)
            Rows.Remove(row);
    }

    protected override async Task RunRoundAsync(CancellationToken ct)
    {
        ProbeRowViewModel[] rows = Rows.ToArray();
        if (rows.Length == 0) return;

        var tasks = rows.Select(r => Ping.ProbeAsync(r.Host, DirectPingTtl, Settings.PingTimeoutMs, ct)).ToArray();
        ProbeResult[] results = await Task.WhenAll(tasks);

        for (int i = 0; i < rows.Length; i++)
        {
            ProbeResult r = results[i];
            if (r.Address is not null)
                rows[i].IpAddress = r.Address.ToString();
            rows[i].AddSample(new PingSample(DateTime.UtcNow, r.RttMs));
        }

        // Periodically refresh the traceroute for the selected destination.
        if (SelectedRow is { } sel && _roundCounter % SelectedTraceEveryRounds == 0)
            await UpdateSelectedTraceAsync(sel.Host, ct);
        _roundCounter++;
    }

    private async Task UpdateSelectedTraceAsync(string host, CancellationToken ct)
    {
        var allHops = await Trace.RunRoundAsync(host, Settings.MaxHops, Settings.PingTimeoutMs, ct);
        var displayHops = Services.TracerouteService.TrimForDisplay(allHops);
        SelectedTraceHops.Clear();
        foreach (var hop in displayHops)
        {
            string ip = hop.Address?.ToString() ?? "*";
            var row = new ProbeRowViewModel(hop.Ttl, ip) { DisplayLabel = $"{hop.Ttl}. {ip}", IpAddress = ip };
            row.AddSample(new PingSample(DateTime.UtcNow, hop.RttMs));
            SelectedTraceHops.Add(row);
        }
    }

    public override void SaveSettings()
    {
        Settings.MultiPingTargets = Rows.Select(r => r.Host).ToList();
        base.SaveSettings();
    }
}
