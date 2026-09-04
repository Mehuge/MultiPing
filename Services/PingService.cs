using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MultiPing.Services;

/// <summary>Result of a single ICMP probe.</summary>
public readonly record struct ProbeResult(IPAddress? Address, double? RttMs, IPStatus Status, bool Reached);

/// <summary>
/// Low-level ICMP probing built on <see cref="Ping"/>. Sending a probe with a specific TTL lets us
/// measure intermediate routers (they reply <see cref="IPStatus.TtlExpired"/>), which is the basis for traceroute.
/// </summary>
public sealed class PingService
{
    // A small, fixed payload similar to what standard ping utilities send.
    private static readonly byte[] Payload = new byte[32];

    private readonly ConcurrentDictionary<string, (IPAddress Address, DateTime ExpiredAt)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DnsCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Resolves a hostname or IP string to an <see cref="IPAddress"/>, preferring IPv4.
    /// Caches resolutions briefly to avoid redundant DNS lookups during concurrent probes.
    /// </summary>
    public async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        if (IPAddress.TryParse(host, out var parsed))
            return parsed;

        DateTime now = DateTime.UtcNow;
        if (_dnsCache.TryGetValue(host, out var cached) && cached.ExpiredAt > now)
            return cached.Address;

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            IPAddress? chosen = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                                ?? addresses.FirstOrDefault();

            if (chosen is not null)
            {
                _dnsCache[host] = (chosen, now + DnsCacheDuration);
            }
            return chosen;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends one ICMP echo request to <paramref name="targetIp"/> with the given <paramref name="ttl"/>.
    /// RTT is measured with a stopwatch because some platforms report 0 for TTL-expired replies.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(IPAddress targetIp, int ttl, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        var options = new PingOptions(ttl, dontFragment: true);
        var sw = Stopwatch.StartNew();
        try
        {
            PingReply reply = await ping.SendPingAsync(targetIp, TimeSpan.FromMilliseconds(timeoutMs), Payload, options, ct)
                .ConfigureAwait(false);
            sw.Stop();

            bool reached = reply.Status == IPStatus.Success;
            bool responded = reached || reply.Status == IPStatus.TtlExpired;
            double? rtt = responded ? sw.Elapsed.TotalMilliseconds : null;
            return new ProbeResult(reply.Address, rtt, reply.Status, reached);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // PingException / socket issues are treated as a lost probe rather than surfaced.
            return new ProbeResult(null, null, IPStatus.Unknown, Reached: false);
        }
    }

    /// <summary>
    /// Sends one ICMP echo request to <paramref name="host"/> with the given <paramref name="ttl"/>.
    /// Resolves the host first, then probes the target IP.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(string host, int ttl, int timeoutMs, CancellationToken ct)
    {
        IPAddress? targetIp = await ResolveAsync(host, ct).ConfigureAwait(false);
        if (targetIp is null)
        {
            return new ProbeResult(null, null, IPStatus.DestinationHostUnreachable, Reached: false);
        }

        return await ProbeAsync(targetIp, ttl, timeoutMs, ct).ConfigureAwait(false);
    }
}
