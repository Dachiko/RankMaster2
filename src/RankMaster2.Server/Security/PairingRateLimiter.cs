using System.Net;

namespace RankMaster2.Server.Security;

/// <summary>
/// Sliding-window rate limit for <c>POST /pair</c>: 5 attempts per minute per source address
/// (SERVER_SPEC.md § 15).
///
/// <para>
/// The address comes from the transport connection and never from a forwarded header. There is no
/// reverse proxy in this product, so an <c>X-Forwarded-For</c> arriving here could only have been
/// written by the attacker — honouring it would hand out an unlimited number of rate-limit buckets.
/// </para>
/// <para>
/// The bucket table is capped. An attacker spoofing source addresses cannot make the server hold an
/// unbounded number of buckets; once the table is full the oldest buckets are evicted, which at
/// worst forgives an attacker who has already been slowed to 5 tries a minute and who must still
/// get past the per-window attempt budget.
/// </para>
/// </summary>
public sealed class PairingRateLimiter
{
    private const int MaxBuckets = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _buckets = new(StringComparer.Ordinal);
    private readonly int _perMinute;

    public PairingRateLimiter(int perMinute) => _perMinute = Math.Max(1, perMinute);

    public static string KeyFor(IPAddress? address)
    {
        if (address is null) return "unknown";

        // ::ffff:192.168.1.5 and 192.168.1.5 are the same host and must share one bucket.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    /// <summary>
    /// Records an attempt. Returns the delta-seconds to wait when the limit is already spent, or
    /// <c>null</c> when the attempt is allowed.
    /// </summary>
    public int? TryAttempt(string key, DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-1);

        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var hits))
            {
                if (_buckets.Count >= MaxBuckets) Prune(cutoff);
                if (_buckets.Count >= MaxBuckets) EvictOldest();
                hits = new List<DateTimeOffset>(_perMinute + 1);
                _buckets[key] = hits;
            }

            hits.RemoveAll(t => t <= cutoff);

            if (hits.Count >= _perMinute)
            {
                var retryAfter = (int)Math.Ceiling((hits[0].AddMinutes(1) - now).TotalSeconds);
                return Math.Max(1, retryAfter);
            }

            hits.Add(now);
            return null;
        }
    }

    private void Prune(DateTimeOffset cutoff)
    {
        var dead = _buckets.Where(kv => kv.Value.Count == 0 || kv.Value[^1] <= cutoff)
                           .Select(kv => kv.Key)
                           .ToList();
        foreach (var key in dead) _buckets.Remove(key);
    }

    private void EvictOldest()
    {
        var oldest = _buckets
            .OrderBy(kv => kv.Value.Count == 0 ? DateTimeOffset.MinValue : kv.Value[^1])
            .Take(_buckets.Count / 4 + 1)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in oldest) _buckets.Remove(key);
    }
}
