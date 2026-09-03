using System;
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
/// Performs traceroute rounds by probing the target with concurrent TTLs. All hops are probed in parallel,
/// and the round ends when the destination replies (<see cref="IPStatus.Success"/>) or <c>maxHops</c> is reached.
/// If the destination is reached, other pending probes are cancelled.
/// </summary>
public sealed class TracerouteService
{
    private readonly PingService _ping;

    public TracerouteService(PingService ping) => _ping = ping;

    public async Task<IReadOnlyList<HopResult>> RunRoundAsync(string host, int maxHops, int timeoutMs, CancellationToken ct)
    {
        // Create a local CTS so we can cancel pending probes once we reach the destination.
        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var resultDict = new Dictionary<int, HopResult>();

        // Pre-populate with empty results for all hops (dropouts).
        for (int i = 1; i <= maxHops; i++)
            resultDict[i] = new HopResult(i, null, null, IPStatus.TimedOut, false);

        var probeTasks = new List<Task>();

        // Fire off all probe tasks concurrently.
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            int currentTtl = ttl; // Capture for closure.
            var task = _ping.ProbeAsync(host, currentTtl, timeoutMs, localCts.Token)
                .ContinueWith(async probeTask =>
                {
                    try
                    {
                        ProbeResult r = await probeTask.ConfigureAwait(false);
                        var result = new HopResult(currentTtl, r.Address, r.RttMs, r.Status, r.Reached);
                        lock (resultDict) resultDict[currentTtl] = result;

                        // If we've reached the destination, cancel all pending probes.
                        if (r.Reached) localCts.Cancel();
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when we reach the destination or the overall operation is cancelled.
                        // Leave the pre-populated TimedOut entry.
                    }
                    catch
                    {
                        // Other exceptions (e.g., network errors) also count as dropouts.
                        // Leave the pre-populated TimedOut entry.
                    }
                }, TaskScheduler.Default).Unwrap();

            probeTasks.Add(task);
        }

        // Wait for all probes to complete (or be cancelled).
        try
        {
            await Task.WhenAll(probeTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when destination is reached.
        }

        // Return ALL results sorted by TTL (no trimming). Trimming is done at display layer.
        return resultDict.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    /// <summary>Trims results for display: up to first destination, or last responding + one dropout, or first one only.</summary>
    public static IReadOnlyList<HopResult> TrimForDisplay(IReadOnlyList<HopResult> allResults)
    {
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
            if (allResults[i].Status != IPStatus.TimedOut)
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
}
