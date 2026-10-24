using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Watchblox.Models;

namespace Watchblox.Services
{
    /// <summary>
    /// Per-user presence history, recorded from every presence poll.
    /// Bounded by design: 168 hour-of-week buckets (online/total samples)
    /// plus per-game sample counts and a last-seen timestamp per user.
    /// Peak hours and most-played games are computed from these buckets,
    /// so no raw per-poll log ever grows without bound.
    /// </summary>
    public class PresenceHistoryService
    {
        private const int Buckets = 168; // 7 days x 24 hours
        private readonly string _path;
        private readonly Dictionary<long, UserHistory> _users = new Dictionary<long, UserHistory>();
        private int _pollsSinceSave;

        public PresenceHistoryService()
        {
            _path = Path.Combine(SettingsService.DataDir, "presence-history.json");
            Load();
        }

        public class UserHistory
        {
            public int[] Online { get; set; } = new int[Buckets];
            public int[] Total { get; set; } = new int[Buckets];
            public Dictionary<long, int> Games { get; set; } = new Dictionary<long, int>();
            public DateTime? LastSeenUtc { get; set; }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var data = JsonSerializer.Deserialize<Dictionary<long, UserHistory>>(File.ReadAllText(_path));
                if (data == null) return;
                foreach (var kv in data)
                {
                    // One bad (null) entry must not discard every user's history.
                    if (kv.Value == null) continue;
                    // Guard against corrupt/short arrays from older writes.
                    if (kv.Value.Online == null || kv.Value.Online.Length != Buckets) kv.Value.Online = new int[Buckets];
                    if (kv.Value.Total == null || kv.Value.Total.Length != Buckets) kv.Value.Total = new int[Buckets];
                    if (kv.Value.Games == null) kv.Value.Games = new Dictionary<long, int>();
                    _users[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_users));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch { }
            _pollsSinceSave = 0;
        }

        /// <summary>
        /// Called once per presence poll (not once per record): persists
        /// at most every ~10 polls; exit always flushes via Save().
        /// </summary>
        public void FlushIfDue()
        {
            if (++_pollsSinceSave >= 10) Save();
        }

        /// <summary>Record one presence poll for one user.</summary>
        public void Record(long userId, PresenceType status, long? universeId, DateTime nowLocal)
        {
            if (!_users.TryGetValue(userId, out var h))
            {
                h = new UserHistory();
                _users[userId] = h;
            }
            int bucket = ((int)nowLocal.DayOfWeek * 24) + nowLocal.Hour;
            h.Total[bucket]++;
            if (status != PresenceType.Offline)
            {
                h.Online[bucket]++;
                h.LastSeenUtc = DateTime.UtcNow;
                if (status == PresenceType.InGame && universeId.HasValue && universeId.Value > 0)
                {
                    h.Games.TryGetValue(universeId.Value, out int n);
                    h.Games[universeId.Value] = n + 1;
                }
            }
            // No save here: Record runs once per friend per poll, so saving
            // here rewrote the whole file ~N/10 times per poll. The caller
            // flushes once per poll via FlushIfDue(); exit flushes via Save().
        }

        public UserHistory Get(long userId) =>
            _users.TryGetValue(userId, out var h) ? h : null;

        public DateTime? LastSeenLocal(long userId)
        {
            var h = Get(userId);
            return h?.LastSeenUtc?.ToLocalTime();
        }

        /// <summary>
        /// Top peak hours by online rate (buckets with at least 8 samples,
        /// so one stray poll can't dominate). Returns (bucket, rate) pairs.
        /// </summary>
        public List<(int bucket, double rate)> PeakHours(long userId, int count = 3)
        {
            var h = Get(userId);
            var result = new List<(int bucket, double rate)>();
            if (h == null) return result;
            for (int i = 0; i < Buckets; i++)
            {
                if (h.Total[i] >= 8 && h.Online[i] > 0)
                    result.Add((i, (double)h.Online[i] / h.Total[i]));
            }
            result.Sort((a, b) => b.rate.CompareTo(a.rate));
            return result.Take(count).ToList();
        }

        public int TotalSamples(long userId)
        {
            var h = Get(userId);
            return h == null ? 0 : h.Total.Sum();
        }

        /// <summary>Most-sampled game universe, or null when nothing recorded.</summary>
        public long? TopGameUniverse(long userId)
        {
            var h = Get(userId);
            if (h == null || h.Games.Count == 0) return null;
            return h.Games.Aggregate((a, b) => a.Value >= b.Value ? a : b).Key;
        }

        public static string FormatBucket(int bucket)
        {
            string[] days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            int day = bucket / 24, hour = bucket % 24;
            string h12 = hour == 0 ? "12 AM" : hour < 12 ? $"{hour} AM" : hour == 12 ? "12 PM" : $"{hour - 12} PM";
            return $"{days[day]} {h12}";
        }

        /// <summary>Short "Last seen" text for a local timestamp.</summary>
        public static string FormatLastSeenShort(DateTime local)
        {
            return local.Date == DateTime.Today
                ? "Today " + local.ToString("h:mm tt")
                : local.Date == DateTime.Today.AddDays(-1)
                    ? "Yesterday " + local.ToString("h:mm tt")
                    : local.ToString("MMM d, yyyy h:mm tt");
        }
    }
}
