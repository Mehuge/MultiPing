using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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

    /// <summary>
    /// Sends one ICMP echo request to <paramref name="host"/> with the given <paramref name="ttl"/>.
    /// RTT is measured with a stopwatch because some platforms report 0 for TTL-expired replies.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(string host, int ttl, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        var options = new PingOptions(ttl, dontFragment: true);
        var sw = Stopwatch.StartNew();
        try
        {
            PingReply reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(timeoutMs), Payload, options, ct)
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
}
