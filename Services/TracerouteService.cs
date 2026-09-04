using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace MultiPing.Services;

/// <summary>Per-hop outcome of a single traceroute round.</summary>
public readonly record struct HopResult(int Ttl, IPAddress? Address, double? RttMs, IPStatus Status, bool Reached);

/// <summary>
/// Performs traceroute rounds by probing target TTLs concurrently.
/// Dynamically adapts the active TTL range based on the discovered destination hop,
/// looking ahead if the route changes, while limiting search depth if the destination is unreachable.
/// </summary>
public sealed class TracerouteService
{
    /// <summary>
    /// Maximum number of hops to look ahead past the last expected destination or last responding router.
    /// </summary>
    public const int DefaultLookaheadLimit = 3;

    private readonly PingService _ping;
    private readonly ConcurrentDictionary<string, TargetTraceState> _stateByHost = new(StringComparer.OrdinalIgnoreCase);

    public TracerouteService(PingService ping) => _ping = ping;

    public void ResetState(string? host = null)
    {
        if (string.IsNullOrEmpty(host))
            _stateByHost.Clear();
        else
            _stateByHost.TryRemove(host, out _);
    }

    public async Task<IReadOnlyList<HopResult>> RunRoundAsync(string host, int maxHops, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return Array.Empty<HopResult>();

        IPAddress? targetIp = await _ping.ResolveAsync(host, ct).ConfigureAwait(false);
        if (targetIp is null)
        {
            return new[] { new HopResult(1, null, null, IPStatus.DestinationHostUnreachable, false) };
        }

        TargetTraceState state = _stateByHost.GetOrAdd(host, _ => new TargetTraceState());
        state.TargetIp = targetIp;

        // Determine how many hops to probe in the initial batch
        int initialMaxTtl;
        if (state.KnownDestinationTtl is int destTtl)
        {
            // Destination was previously found at destTtl. Probe 1..destTtl concurrently.
            initialMaxTtl = Math.Clamp(destTtl, 1, maxHops);
        }
        else if (state.LastRespondingTtl is int lastRespTtl)
        {
            // Destination was not reached, but we know routers responded up to lastRespTtl.
            // Look ahead by DefaultLookaheadLimit past last responding hop, bounded by maxHops.
            initialMaxTtl = Math.Clamp(lastRespTtl + DefaultLookaheadLimit, 1, maxHops);
        }
        else
        {
            // Initial round or full discovery: probe all hops up to maxHops concurrently.
            initialMaxTtl = maxHops;
        }

        // Fire all initial hop probes concurrently
        var initialTasks = new Task<HopResult>[initialMaxTtl];
        for (int i = 0; i < initialMaxTtl; i++)
        {
            int ttl = i + 1;
            initialTasks[i] = ProbeHopAsync(targetIp, ttl, timeoutMs, ct);
        }

        HopResult[] initialResults = await Task.WhenAll(initialTasks).ConfigureAwait(false);

        int destIndex = -1;
        int highestRespondingTtl = 0;

        for (int i = 0; i < initialResults.Length; i++)
        {
            var h = initialResults[i];
            if (h.Status != IPStatus.TimedOut && h.Status != IPStatus.Unknown && h.Address is not null)
            {
                if (h.Ttl > highestRespondingTtl)
                    highestRespondingTtl = h.Ttl;
            }

            if (h.Reached && destIndex == -1)
            {
                destIndex = i;
            }
        }

        if (destIndex != -1)
        {
            // Destination reached within initial batch!
            int reachedTtl = initialResults[destIndex].Ttl;
            state.KnownDestinationTtl = reachedTtl;
            state.LastRespondingTtl = reachedTtl;
            return initialResults.Take(destIndex + 1).ToList();
        }

        // Destination was NOT reached in the initial batch (route changed, transient loss, or unreachable).
        // Check if we can probe lookahead hops to find the host.
        int currentMaxProbed = initialMaxTtl;
        int lookaheadEnd = Math.Min(Math.Max(currentMaxProbed, highestRespondingTtl) + DefaultLookaheadLimit, maxHops);

        if (lookaheadEnd > currentMaxProbed)
        {
            int extraCount = lookaheadEnd - currentMaxProbed;
            var lookaheadTasks = new Task<HopResult>[extraCount];
            for (int i = 0; i < extraCount; i++)
            {
                int ttl = currentMaxProbed + 1 + i;
                lookaheadTasks[i] = ProbeHopAsync(targetIp, ttl, timeoutMs, ct);
            }

            HopResult[] lookaheadResults = await Task.WhenAll(lookaheadTasks).ConfigureAwait(false);

            int lookaheadDestIndex = -1;
            for (int i = 0; i < lookaheadResults.Length; i++)
            {
                var h = lookaheadResults[i];
                if (h.Status != IPStatus.TimedOut && h.Status != IPStatus.Unknown && h.Address is not null)
                {
                    if (h.Ttl > highestRespondingTtl)
                        highestRespondingTtl = h.Ttl;
                }

                if (h.Reached && lookaheadDestIndex == -1)
                {
                    lookaheadDestIndex = i;
                }
            }

            var combinedResults = initialResults.Concat(lookaheadResults).ToList();

            if (lookaheadDestIndex != -1)
            {
                // Destination found in lookahead!
                int reachedTtl = lookaheadResults[lookaheadDestIndex].Ttl;
                state.KnownDestinationTtl = reachedTtl;
                state.LastRespondingTtl = reachedTtl;
                int totalDestIndex = initialResults.Length + lookaheadDestIndex;
                return combinedResults.Take(totalDestIndex + 1).ToList();
            }
            else
            {
                // Lookahead completed without finding destination. Host is unreachable or beyond limit.
                state.KnownDestinationTtl = null;
                state.LastRespondingTtl = highestRespondingTtl > 0 ? highestRespondingTtl : null;
                return combinedResults;
            }
        }
        else
        {
            // Already at maxHops or reached lookahead boundary without finding destination.
            state.KnownDestinationTtl = null;
            state.LastRespondingTtl = highestRespondingTtl > 0 ? highestRespondingTtl : null;
            return initialResults;
        }
    }

    private async Task<HopResult> ProbeHopAsync(IPAddress targetIp, int ttl, int timeoutMs, CancellationToken ct)
    {
        ProbeResult r = await _ping.ProbeAsync(targetIp, ttl, timeoutMs, ct).ConfigureAwait(false);
        bool reached = r.Reached || (r.Address is not null && r.Address.Equals(targetIp));
        return new HopResult(ttl, r.Address, r.RttMs, r.Status, reached);
    }

    /// <summary>Trims results for display: up to first destination, or last responding + one dropout, or first one only.</summary>
    public static IReadOnlyList<HopResult> TrimForDisplay(IReadOnlyList<HopResult> allResults)
    {
        if (allResults.Count == 0) return allResults;

        // First, check if we reached the destination.
        for (int i = 0; i < allResults.Count; i++)
        {
            if (allResults[i].Reached)
            {
                // Found the destination, stop here.
                return allResults.Take(i + 1).ToList();
            }
        }

        // No destination reached. Find the last responding hop and include one dropout after it.
        for (int i = allResults.Count - 1; i >= 0; i--)
        {
            if (allResults[i].Status != IPStatus.TimedOut && allResults[i].Status != IPStatus.Unknown && allResults[i].Address is not null)
            {
                // Found a responding hop, keep up to here + one more for dropout if exists.
                int lastKeepIndex = i;
                if (i + 1 < allResults.Count) lastKeepIndex = i + 1;
                return allResults.Take(lastKeepIndex + 1).ToList();
            }
        }

        // All are dropouts, keep just the first one.
        return allResults.Take(1).ToList();
    }

    private sealed class TargetTraceState
    {
        public IPAddress? TargetIp { get; set; }
        public int? KnownDestinationTtl { get; set; }
        public int? LastRespondingTtl { get; set; }
    }
}
