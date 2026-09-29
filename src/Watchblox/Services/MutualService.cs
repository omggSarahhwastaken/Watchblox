using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Watchblox.Services
{
    /// <summary>
    /// Mutual-friend intersections computed locally from public friend lists.
    /// Per-user friend sets are cached (1h TTL); a private profile simply
    /// yields an empty set. Callers stagger their requests so the API is
    /// never hammered — a couple of lookups per minute at most.
    /// </summary>
    public class MutualService
    {
        private readonly RobloxApiClient _api;
        private readonly Dictionary<long, (HashSet<long> ids, DateTime fetchedAt)> _cache
            = new Dictionary<long, (HashSet<long>, DateTime)>();
        private readonly object _lock = new object();
        public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(1);

        public MutualService(RobloxApiClient api) { _api = api; }

        public void Forget(long userId) { lock (_lock) _cache.Remove(userId); }
        public void Clear() { lock (_lock) _cache.Clear(); }

        public bool IsCached(long userId)
        {
            lock (_lock)
                return _cache.TryGetValue(userId, out var rec)
                    && DateTime.UtcNow - rec.fetchedAt < Ttl;
        }

        public async Task<HashSet<long>> GetFriendIdsAsync(long userId)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(userId, out var rec) && DateTime.UtcNow - rec.fetchedAt < Ttl)
                    return rec.ids;
            }
            HashSet<long> result;
            try { result = new HashSet<long>(await _api.GetFriendIdsAsync(userId)); }
            catch (RobloxApiException) { result = new HashSet<long>(); } // private profile
            lock (_lock) _cache[userId] = (result, DateTime.UtcNow);
            return result;
        }

        public async Task<List<long>> GetMutualIdsAsync(long userId, HashSet<long> myFriendIds)
        {
            var theirs = await GetFriendIdsAsync(userId);
            var mutual = theirs.Intersect(myFriendIds).ToList();
            mutual.Remove(userId); // never list the person themself
            return mutual;
        }
    }
}
