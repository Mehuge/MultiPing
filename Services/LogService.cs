using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MultiPing.Models;

namespace MultiPing.Services;

/// <summary>One row to be written to the log, mirroring a grid row.</summary>
public readonly record struct LogEntry(int Index, string IpAddress, SeriesStatistics Stats);

/// <summary>
/// Appends trace rows to a log file while logging is enabled. Output is fixed-width text that mirrors
/// the on-screen grid (Hop/Destination, IP, RTT, Min, Max, Avg, PL%) with a leading timestamp column.
/// A new file is created each time logging is turned on; entries append until it is turned off.
/// </summary>
public sealed class LogService : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private AppMode _mode;

    public bool IsOpen => _writer is not null;
    public string? CurrentPath { get; private set; }

    /// <summary>Opens a fresh timestamped log file in <paramref name="directory"/> and writes the header.</summary>
    public void Open(AppMode mode, string directory, DateTime nowLocal)
    {
        lock (_gate)
        {
            Close_NoLock();
            _mode = mode;
            Directory.CreateDirectory(directory);

            string prefix = mode == AppMode.PlotPing ? "plotping" : "multiping";
            string name = $"{prefix}_{nowLocal:yyyyMMdd_HHmmss}.log";
            CurrentPath = Path.Combine(directory, name);

            _writer = new StreamWriter(CurrentPath, append: true) { AutoFlush = true };
            _writer.WriteLine($"# MultiPing {mode} log started {nowLocal:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine(Header(mode));
        }
    }

    /// <summary>Writes one round of entries, each stamped with <paramref name="timestampLocal"/>.</summary>
    public void WriteRound(DateTime timestampLocal, IEnumerable<LogEntry> entries)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            foreach (var e in entries)
                _writer.WriteLine(FormatRow(timestampLocal, e));
        }
    }

    public void Close()
    {
        lock (_gate) Close_NoLock();
    }

    private void Close_NoLock()
    {
        if (_writer is not null)
        {
            try { _writer.Flush(); _writer.Dispose(); } catch { /* best effort */ }
            _writer = null;
            CurrentPath = null;
        }
    }

    private string Header(AppMode mode)
    {
        string first = mode == AppMode.PlotPing ? "Hop" : "Dest";
        return $"{Truncate("Timestamp", 23),-23}" +
           $" {Truncate(first, 5),-5}" +
           $" {Truncate("IP Address", 24),-24}" +
           $" {"RTT",9}" +
           $" {"Min",9}" +
           $" {"Max",9}" +
           $" {"Avg",9}" +
           $" {"PL%",7}";
    }

    private static string FormatRow(DateTime ts, LogEntry e)
    {
        var s = e.Stats;
        return $"{ts.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),-23}" +
           $" {e.Index.ToString(CultureInfo.InvariantCulture),-5}" +
           $" {PadOrTruncate(e.IpAddress, 24),-24}" +
           $" {Num(s.Last),9}" +
           $" {Num(s.Min),9}" +
           $" {Num(s.Max),9}" +
           $" {Num(s.Avg),9}" +
           $" {s.PacketLossPercent.ToString("0.0", CultureInfo.InvariantCulture),7}";
    }

    private static string Num(double? v) =>
        v is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "-";

    private static string PadOrTruncate(string s, int width) =>
        s.Length > width ? s[..width] : s;

    private static string Truncate(string s, int width) =>
        s.Length > width ? s[..width] : s;
    
    public void Dispose() => Close();
}
