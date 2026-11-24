using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Watchblox.Services
{
    /// <summary>
    /// Mutual-friend intersections computed locally from public friend lists.
    /// Per-user friend sets are cached (1h TTL); a private profile simply
    /// yields an empty set. Live fetches are paced (at least 1s between
    /// one finishing and the next starting) so a fan-out like the network
    /// scan can't trip Roblox's rate limit; cached hits skip the gate
    /// entirely, so rebuilds stay instant.
    /// </summary>
    public class MutualService
    {
        private readonly RobloxApiClient _api;
        private readonly Dictionary<long, (HashSet<long> ids, DateTime fetchedAt)> _cache
            = new Dictionary<long, (HashSet<long>, DateTime)>();
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _paceGate = new SemaphoreSlim(1, 1);
        private DateTime _lastFetchUtc = DateTime.MinValue;
        private const int MinFetchGapMs = 1000;
        public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(1);

        public MutualService(RobloxApiClient api) { _api = api; }

        public void Forget(long userId) { lock (_lock) _cache.Remove(userId); }
        public void Clear() { lock (_lock) _cache.Clear(); }

        public async Task<HashSet<long>> GetFriendIdsAsync(long userId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(userId, out var rec) && DateTime.UtcNow - rec.fetchedAt < Ttl)
                    return rec.ids;
            }
            HashSet<long> result;
            await _paceGate.WaitAsync(ct);
            try
            {
                var gap = DateTime.UtcNow - _lastFetchUtc;
                if (gap.TotalMilliseconds < MinFetchGapMs)
                    await Task.Delay(MinFetchGapMs - (int)gap.TotalMilliseconds, ct);
                try { result = new HashSet<long>(await _api.GetFriendIdsAsync(userId)); }
                catch (RobloxAuthRequiredException) { throw; } // login needed — never an empty set, never cached
                catch (RobloxRateLimitedException)
                {
                    // Limited right now, not private: let the caller stop
                    // early (NetworkService) or report it (mutuals page).
                    // Never cached, so a later rebuild retries.
                    _lastFetchUtc = DateTime.UtcNow;
                    throw;
                }
                catch (RobloxApiException) { result = new HashSet<long>(); } // private profile (logged in) — genuinely empty
                _lastFetchUtc = DateTime.UtcNow;
            }
            finally { _paceGate.Release(); }
            lock (_lock) _cache[userId] = (result, DateTime.UtcNow);
            return result;
        }

        public async Task<List<long>> GetMutualIdsAsync(long userId, HashSet<long> myFriendIds, CancellationToken ct = default)
        {
            var theirs = await GetFriendIdsAsync(userId, ct);
            var mutual = theirs.Intersect(myFriendIds).ToList();
            mutual.Remove(userId); // never list the person themself
            return mutual;
        }
    }
}
