using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Watchblox.Services
{
    public class PresenceTransition
    {
        public long UserId;
        public int FromType;
        public int ToType;
        public string GameName = "";
        public long? PlaceId;
        public string GameServerId = "";
        public DateTime Time = DateTime.Now;

        public string Describe(string displayName)
        {
            if (FromType == 2 && ToType == 2)
                return string.IsNullOrEmpty(GameName) ? "switched games" : "switched to " + GameName;
            return ToType switch
            {
                0 => "went offline",
                1 => FromType == 2 ? "left " + (string.IsNullOrEmpty(GameName) ? "their game" : GameName) : "came online",
                2 => string.IsNullOrEmpty(GameName) ? "started playing" : "joined " + GameName,
                3 => "opened Roblox Studio",
                _ => "changed status"
            };
        }

        public string Kind => ToType switch
        {
            0 => "offline",
            2 => "game",
            3 => "studio",
            _ => "online"
        };
    }

    /// <summary>
    /// Polls presence on a timer interval and emits only transitions.
    /// The first poll seeds state silently (no event storm on startup).
    /// </summary>
    public class PresenceMonitor
    {
        private readonly Dictionary<long, PresenceInfo> _last = new Dictionary<long, PresenceInfo>();
        private bool _seeded;

        public event Action<List<PresenceTransition>> Transitions;

        public IReadOnlyDictionary<long, PresenceInfo> Last => _last;

        public async Task<List<PresenceTransition>> PollAsync(RobloxApiClient api, IEnumerable<long> ids)
        {
            var transitions = new List<PresenceTransition>();
            var requested = new HashSet<long>(ids);
            var infos = await api.GetPresenceAsync(ids);
            var returned = new HashSet<long>();
            foreach (var p in infos)
            {
                returned.Add(p.UserId);
                if (_last.TryGetValue(p.UserId, out var old))
                {
                    var t = Diff(old, p);
                    if (t != null) transitions.Add(t);
                }
                else if (_seeded)
                {
                    // Newly tracked user appearing mid-session: treat as came online if online.
                    if (p.Type != 0)
                        transitions.Add(new PresenceTransition
                        {
                            UserId = p.UserId, FromType = 0, ToType = p.Type,
                            GameName = p.LastLocation, PlaceId = p.PlaceId, GameServerId = p.GameId
                        });
                }
                _last[p.UserId] = p;
            }
            // Requested ids missing from the response must not keep a stale
            // online status: mark them offline (emitting a transition if
            // they were online), instead of leaving the last status forever.
            foreach (var id in requested)
            {
                if (returned.Contains(id)) continue;
                if (_last.TryGetValue(id, out var old) && old.Type != 0)
                {
                    var t = Diff(old, new PresenceInfo { UserId = id, Type = 0 });
                    if (t != null) transitions.Add(t);
                    _last[id] = new PresenceInfo { UserId = id, Type = 0 };
                }
            }
            // Prune ids no longer requested, so dropped users never linger.
            foreach (var id in _last.Keys.ToList())
                if (!requested.Contains(id)) _last.Remove(id);
            _seeded = true;
            if (transitions.Count > 0) Transitions?.Invoke(transitions);
            return transitions;
        }

        private static PresenceTransition Diff(PresenceInfo old, PresenceInfo cur)
        {
            bool typeChanged = old.Type != cur.Type;
            bool gameChanged = old.Type == 2 && cur.Type == 2 && old.PlaceId != cur.PlaceId;
            if (!typeChanged && !gameChanged) return null;
            return new PresenceTransition
            {
                UserId = cur.UserId,
                FromType = old.Type,
                ToType = cur.Type,
                GameName = cur.LastLocation,
                PlaceId = cur.PlaceId,
                GameServerId = cur.GameId
            };
        }

        public void Forget(long userId) => _last.Remove(userId);

        /// <summary>Clears all tracked state (e.g. on account switch), so
        /// the next poll seeds silently instead of firing "came online"
        /// transitions for the new account's friends.</summary>
        public void Clear()
        {
            _last.Clear();
            _seeded = false;
        }
    }
}
