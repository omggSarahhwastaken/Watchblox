using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Watchblox.Models;

namespace Watchblox.Services
{
    /// <summary>ID -> (username, displayName, verified) cache. Only unknown or
    /// stale (&gt;7 days) IDs hit the API.</summary>
    public class NameCache
    {
        private readonly string _path;
        private Dictionary<long, NameRecord> _map = new Dictionary<long, NameRecord>();

        public NameCache()
        {
            _path = Path.Combine(SettingsService.DataDir, "names.json");
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var list = JsonSerializer.Deserialize<List<NameRecord>>(File.ReadAllText(_path));
                if (list != null) _map = list.ToDictionary(n => n.Id);
            }
            catch { _map = new Dictionary<long, NameRecord>(); }
        }

        public void Save()
        {
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_map.Values.ToList())); }
            catch { }
        }

        public NameRecord Get(long id)
        {
            _map.TryGetValue(id, out var r);
            return r;
        }

        // Names only come from friends/mutuals/activity, but never let the
        // cache grow without bound: evict the stalest entries past the cap.
        private const int Cap = 5000;

        public void Put(NameRecord r)
        {
            _map[r.Id] = r;
            if (_map.Count > Cap)
            {
                foreach (var id in _map.Values
                    .OrderBy(v => v.ResolvedAtUnix)
                    .Take(_map.Count - Cap)
                    .Select(v => v.Id)
                    .ToList())
                    _map.Remove(id);
            }
        }

        public List<long> MissingOrStale(IEnumerable<long> ids)
        {
            long weekAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400;
            var out_ = new List<long>();
            foreach (var id in ids.Distinct())
            {
                if (!_map.TryGetValue(id, out var r) || r.ResolvedAtUnix < weekAgo)
                    out_.Add(id);
            }
            return out_;
        }
    }
}
