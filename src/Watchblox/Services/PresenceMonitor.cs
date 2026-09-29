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
            var infos = await api.GetPresenceAsync(ids);
            foreach (var p in infos)
            {
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
    }
}
