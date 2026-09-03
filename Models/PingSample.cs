using System;

namespace MultiPing.Models;

/// <summary>A single latency measurement taken at a point in time. A null <see cref="RttMs"/> represents a lost packet / timeout.</summary>
public readonly struct PingSample
{
    public PingSample(DateTime timestampUtc, double? rttMs)
    {
        TimestampUtc = timestampUtc;
        RttMs = rttMs;
    }

    public DateTime TimestampUtc { get; }

    /// <summary>Round-trip time in milliseconds, or null when the probe timed out / was lost.</summary>
    public double? RttMs { get; }

    public bool IsLost => RttMs is null;
}
