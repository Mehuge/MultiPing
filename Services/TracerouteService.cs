using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MultiPing.Models;

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
    /// <summary>Hops to probe on the very first round, before any state is known. Keeps the
    /// initial trace cheap; subsequent rounds grow from there toward <c>maxHops</c>.</summary>
    private const int InitialProbeHops = 10;

    private readonly PingService _ping;
    private readonly ConcurrentDictionary<string, TargetTraceState> _stateByHost = new(StringComparer.OrdinalIgnoreCase);

    public TracerouteService(PingService ping)  => _ping = ping;

    public void ResetState(string? host = null)
    {
        if (string.IsNullOrEmpty(host))
            _stateByHost.Clear();
        else
            _stateByHost.TryRemove(host, out _);
    }

    public async Task<IReadOnlyList<HopResult>> RunRoundAsync(string host, int maxHops, int timeoutMs, int lookAheadLimit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return Array.Empty<HopResult>();

        TargetTraceState state = _stateByHost.GetOrAdd(host, _ => new TargetTraceState());

        // Resolve the hostname to an IP only once per trace session. The IP is cached in
        // TargetTraceState and reused for all subsequent rounds. Re-resolution only happens
        // when ResetState is called (i.e., user stops and starts the trace).
        if (state.TargetIp is null)
        {
            IPAddress? targetIp = await _ping.ResolveAsync(host, ct).ConfigureAwait(false);
            if (targetIp is null)
            {
                return new[] { new HopResult(1, null, null, IPStatus.DestinationHostUnreachable, false) };
            }
            state.TargetIp = targetIp;
        }

        // On macOS the .NET Ping implementation reports the *target* address for every reply,
        // including ICMP Time Exceeded from intermediate routers, so per-hop addresses collapse
        // to the destination and the trace degenerates to a single hop. The system traceroute
        // binary is setuid root and returns correct router addresses, so delegate to it.
        if (OperatingSystem.IsMacOS())
        {
            var nativeHops = await RunNativeTracerouteAsync(state.TargetIp, maxHops, lookAheadLimit, state, ct).ConfigureAwait(false);
            if (nativeHops.Count > 0)
            {
                int? reachedTtl = null;
                int highestNativeRespondingTtl = 0;
                for (int i = 0; i < nativeHops.Count; i++)
                {
                    var h = nativeHops[i];
                    if (h.Reached && reachedTtl is null) reachedTtl = h.Ttl;
                    if (h.Status != IPStatus.TimedOut && h.Status != IPStatus.Unknown && h.Address is not null)
                    {
                        if (h.Ttl > highestNativeRespondingTtl) highestNativeRespondingTtl = h.Ttl;
                    }
                }

                state.KnownDestinationTtl = reachedTtl;
                state.LastRespondingTtl = reachedTtl is int r ? r : (highestNativeRespondingTtl > 0 ? highestNativeRespondingTtl : state.LastRespondingTtl);
                // Remember the deepest hop we probed this round so the next round's -m can grow toward
                // maxHops instead of resetting to 1 and burning 30s on a non-responding destination.
                state.HighestProbedTtl = nativeHops.Count > 0 ? nativeHops[^1].Ttl : state.HighestProbedTtl;
                return nativeHops;
            }
            // Fall through to the managed probe path if native tracing failed for any reason.
        }

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
            initialMaxTtl = Math.Clamp(lastRespTtl + lookAheadLimit, 1, maxHops);
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
            initialTasks[i] = ProbeHopAsync(state.TargetIp, ttl, timeoutMs, ct);
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
        int lookaheadEnd = Math.Min(Math.Max(currentMaxProbed, highestRespondingTtl) + lookAheadLimit, maxHops);

        if (lookaheadEnd > currentMaxProbed)
        {
            int extraCount = lookaheadEnd - currentMaxProbed;
            var lookaheadTasks = new Task<HopResult>[extraCount];
            for (int i = 0; i < extraCount; i++)
            {
                int ttl = currentMaxProbed + 1 + i;
                lookaheadTasks[i] = ProbeHopAsync(state.TargetIp, ttl, timeoutMs, ct);
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

    /// <summary>
    /// Runs the system <c>traceroute</c> binary on macOS, which is setuid root and returns
    /// correct router addresses for each hop (the managed Ping path cannot). Output is parsed
    /// from the <c>-n -q 1 -w timeout</c> form so each line is one hop with a single RTT.
    /// </summary>
    private async Task<IReadOnlyList<HopResult>> RunNativeTracerouteAsync(
        IPAddress targetIp, int maxHops, int lookAheadLimit, TargetTraceState state, CancellationToken ct)
    {
        // Prefer the resolved IP so the trace is not perturbed by DNS; traceroute re-resolves
        // internally if given a hostname.
        string targetArg = targetIp.ToString();

        // Cap the per-probe wait at 1s. Each unresponsive hop costs a full wait period, so a
        // 2s timeout makes a route with several dropouts take 5-6s; 1s keeps the UI responsive
        // while still being long enough to distinguish real loss from transient latency.
        const int waitSeconds = 1;

        // Adaptive max-hops: each round re-probes every known hop (so the plot gets a fresh sample
        // for each) and extends a little further. For a destination that never responds this makes
        // -m climb toward maxHops over successive rounds instead of burning 30s every round.
        int maxTtl = state.KnownDestinationTtl is int destTtl
            ? destTtl
            : state.HighestProbedTtl is int probedTtl
                ? Math.Min(probedTtl + lookAheadLimit, maxHops)
                : Math.Min(InitialProbeHops, maxHops); // first round: probe a small starting batch
        maxTtl = Math.Max(maxTtl, 1);

        var psi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/traceroute",
            Arguments = $"-I -n -m {maxTtl} -q 1 -w {waitSeconds} {targetArg}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception)
        {
            // Binary missing or not executable: fall back to the managed path.
            return Array.Empty<HopResult>();
        }

        try
        {
            using (ct.Register(() => { try { process.Kill(); } catch { } }))
            {
                string stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                _ = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

                await process.WaitForExitAsync(ct).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    // Non-zero often means a hop timed out before reaching the destination, which is
                    // normal for traceroute; only treat fatal errors (no usable lines) as a failure.
                    if (string.IsNullOrWhiteSpace(stdout))
                        return Array.Empty<HopResult>();
                }

                return ParseNativeTraceroute(stdout, targetIp);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    // Group 1 = hop number, group 2 = address (IP or "*"), group 3 = single RTT in ms.
        // The RTT group is optional: a dropout line is "N  *" with no trailing whitespace, so the
        // whitespace+RTT portion must be optional too or those lines are silently dropped.
        private static readonly Regex HopLine = new(
            @"^\s*(\d+)\s+(\S+)(?:\s+(?:(\d+(?:\.\d+)?)\s*ms))?",
            RegexOptions.Compiled);

    /// <summary>Parses one hop per line from <c>traceroute -n -q 1</c> output.</summary>
    private static List<HopResult> ParseNativeTraceroute(string stdout, IPAddress? targetIp)
    {
        var results = new List<HopResult>();
        foreach (string rawLine in stdout.Replace("\r", string.Empty).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            Match m = HopLine.Match(line);
            if (!m.Success) continue;

            if (!int.TryParse(m.Groups[1].Value, out int ttl) || ttl <= 0)
                continue;

            string addrText = m.Groups[2].Value;
            IPAddress? addr = null;
            if (addrText != "*" && IPAddress.TryParse(addrText, out IPAddress? parsed))
                addr = parsed;

            double? rtt = null;
            if (m.Groups[3].Success && double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                rtt = ms;

            IPStatus status = addr is null ? IPStatus.TimedOut : IPStatus.Success;
            bool reached = addr is not null && (targetIp is null || addr.Equals(targetIp));

            results.Add(new HopResult(ttl, addr, rtt, status, reached));
        }
        return results;
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

        /// <summary>The highest hop TTL this trace has ever probed. Used to grow <c>-m</c> toward
        /// <see cref="maxHops"/> over successive rounds for destinations that never respond.</summary>
        public int? HighestProbedTtl { get; set; }
    }
}
