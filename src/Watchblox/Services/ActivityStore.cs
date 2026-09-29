using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Watchblox.Models;

namespace Watchblox.Services
{
    /// <summary>Append-only activity feed, capped at 500 events, persisted.</summary>
    public class ActivityStore
    {
        private const int Cap = 500;
        private readonly string _path;
        private readonly List<ActivityEvent> _events = new List<ActivityEvent>();

        public IReadOnlyList<ActivityEvent> Events => _events;

        public ActivityStore()
        {
            _path = Path.Combine(SettingsService.DataDir, "activity.json");
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var list = JsonSerializer.Deserialize<List<ActivityEvent>>(File.ReadAllText(_path));
                if (list != null)
                {
                    _events.AddRange(list.OrderByDescending(e => e.Time).Take(Cap));
                    _events.Sort((a, b) => b.Time.CompareTo(a.Time));
                }
            }
            catch { }
        }

        public void Add(ActivityEvent e)
        {
            _events.Insert(0, e);
            while (_events.Count > Cap) _events.RemoveAt(_events.Count - 1);
            Save();
        }

        public void Clear()
        {
            _events.Clear();
            Save();
        }

        private void Save()
        {
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_events)); }
            catch { }
        }
    }
}
