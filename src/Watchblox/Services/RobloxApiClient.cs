using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Watchblox.Models;

namespace Watchblox.Services
{
    public class ResolvedUser
    {
        public long Id;
        public string Username = "";
        public string DisplayName = "";
        public bool Verified;
    }

    public class PresenceInfo
    {
        public long UserId;
        public int Type; // 0 offline, 1 online, 2 in game, 3 in studio
        public string LastLocation = "";
        public long? PlaceId;
        public long? RootPlaceId;
        public string GameId = "";
        public long? UniverseId;
    }

    /// <summary>
    /// A deliberate API answer (private profile, bad request, …), not a
    /// network problem. Never retried, never rewritten into a generic message.
    /// </summary>
    public class RobloxApiException : Exception
    {
        public RobloxApiException(string message) : base(message) { }
    }

    /// <summary>
    /// Roblox API client. Works without credentials, but a session cookie
    /// (set via SetSessionCookie) makes presence accurate: Roblox only shows
    /// real online status to friends, so anonymous presence often reads
    /// offline. The cookie is never logged or placed in a URL.
    /// </summary>
    public class RobloxApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly CookieContainer _cookies = new CookieContainer();
        private int _backoffSeconds = 0;

        public bool IsLoggedIn { get; private set; }

        public event Action<string> StatusChanged;
        private void Status(string s) => StatusChanged?.Invoke(s);

        /// <summary>Raised once when the server rejects the session cookie.</summary>
        public event Action SessionExpired;

        public RobloxApiClient()
        {
            var handler = new HttpClientHandler { CookieContainer = _cookies, UseCookies = true };
            _http = new HttpClient(handler);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Watchblox/1.1");
            _http.Timeout = TimeSpan.FromSeconds(25);
        }

        public void Dispose() => _http.Dispose();

        /// <summary>Attaches a .ROBLOSECURITY cookie to all Roblox requests.</summary>
        public void SetSessionCookie(string cookie)
        {
            _cookies.Add(new Cookie(".ROBLOSECURITY", cookie, "/", ".roblox.com"));
            IsLoggedIn = true;
        }

        public void ClearSessionCookie()
        {
            // Expire it out of the container; the plaintext is simply dropped.
            _cookies.SetCookies(new Uri("https://www.roblox.com"),
                ".ROBLOSECURITY=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");
            IsLoggedIn = false;
        }

        private void OnUnauthorized()
        {
            if (!IsLoggedIn) return;
            ClearSessionCookie();
            try { SessionExpired?.Invoke(); } catch { }
        }

        private async Task<T> WithRetry<T>(Func<Task<T>> call, string what)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    var result = await call();
                    _backoffSeconds = 0;
                    return result;
                }
                catch (RobloxApiException) { throw; }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The session cookie was rejected: stop using it and tell
                    // the UI once. Never retried, never rewritten as a
                    // generic connectivity failure.
                    OnUnauthorized();
                    throw new RobloxApiException("Roblox session expired — log in again.");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
                {
                    attempt++;
                    _backoffSeconds = Math.Min(60, (int)Math.Pow(2, attempt) * 5);
                    Status($"Rate limited — retrying in {_backoffSeconds}s…");
                    await Task.Delay(_backoffSeconds * 1000);
                }
                catch (Exception)
                {
                    if (attempt < 2) { attempt++; await Task.Delay(2000 * attempt); continue; }
                    throw new Exception("Couldn't reach Roblox (" + what + "). Check your connection.");
                }
            }
        }

        private static long? OptLong(JsonElement e, string name)
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l))
                return l;
            return null;
        }

        private static string OptString(JsonElement e, string name)
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
            return "";
        }

        // --- authenticated user (proves the session cookie works) -----------
        public Task<ResolvedUser> GetAuthenticatedUserAsync() =>
            WithRetry(async () =>
            {
                var resp = await _http.GetAsync("https://users.roblox.com/v1/users/authenticated");
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                return new ResolvedUser
                {
                    Id = root.GetProperty("id").GetInt64(),
                    Username = OptString(root, "name"),
                    DisplayName = OptString(root, "displayName"),
                    Verified = root.TryGetProperty("hasVerifiedBadge", out var vb) && vb.ValueKind == JsonValueKind.True
                };
            }, "session check");

        // --- username -> ID -------------------------------------------------
        public Task<ResolvedUser> ResolveUsernameAsync(string username) =>
            WithRetry(async () =>
            {
                var body = JsonSerializer.Serialize(new { usernames = new[] { username }, excludeBannedUsers = true });
                var resp = await _http.PostAsync("https://users.roblox.com/v1/usernames/users",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    return new ResolvedUser
                    {
                        Id = u.GetProperty("id").GetInt64(),
                        Username = OptString(u, "name"),
                        DisplayName = OptString(u, "displayName"),
                        Verified = u.TryGetProperty("hasVerifiedBadge", out var vb) && vb.ValueKind == JsonValueKind.True
                    };
                }
                return null;
            }, "username lookup");

        // --- friend IDs (paginated, cap is 1000) -----------------------------
        public Task<List<long>> GetFriendIdsAsync(long userId) =>
            WithRetry(async () =>
            {
                var ids = new List<long>();
                string cursor = "";
                int pages = 0;
                do
                {
                    string url = $"https://friends.roblox.com/v1/users/{userId}/friends?limit=100"
                        + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
                    var resp = await _http.GetAsync(url);
                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                        throw new RobloxApiException("This Roblox profile is private — friend list unavailable.");
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    foreach (var f in root.GetProperty("data").EnumerateArray())
                        if (f.TryGetProperty("id", out var id) && id.TryGetInt64(out var l)) ids.Add(l);
                    cursor = OptString(root, "nextPageCursor");
                    pages++;
                } while (!string.IsNullOrEmpty(cursor) && ids.Count < 1000 && pages < 12);
                return ids;
            }, "friend list");

        // --- full user profile (bio, join date) ---------------------------------
        public Task<UserProfile> GetUserProfileAsync(long userId) =>
            WithRetry(async () =>
            {
                var resp = await _http.GetAsync($"https://users.roblox.com/v1/users/{userId}");
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    throw new RobloxApiException("Roblox user not found.");
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                DateTime created = DateTime.MinValue;
                if (root.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.String)
                    DateTime.TryParse(c.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out created);
                return new UserProfile
                {
                    Id = root.GetProperty("id").GetInt64(),
                    Username = OptString(root, "name"),
                    DisplayName = OptString(root, "displayName"),
                    Description = OptString(root, "description"),
                    Created = created,
                    HasVerifiedBadge = root.TryGetProperty("hasVerifiedBadge", out var vb)
                        && vb.ValueKind == JsonValueKind.True
                };
            }, "profile");

        // --- groups a user is in (with their role) ---------------------------
        public Task<List<UserGroupInfo>> GetUserGroupsAsync(long userId) =>
            WithRetry(async () =>
            {
                var list = new List<UserGroupInfo>();
                string cursor = "";
                do
                {
                    string url = $"https://groups.roblox.com/v2/users/{userId}/groups/roles?limit=100"
                        + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
                    var resp = await _http.GetAsync(url);
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    foreach (var g in root.GetProperty("data").EnumerateArray())
                    {
                        var grp = g.GetProperty("group");
                        var role = g.GetProperty("role");
                        list.Add(new UserGroupInfo
                        {
                            GroupId = grp.GetProperty("id").GetInt64(),
                            GroupName = OptString(grp, "name"),
                            MemberCount = grp.TryGetProperty("memberCount", out var mc)
                                && mc.TryGetInt32(out var m) ? m : 0,
                            RoleName = OptString(role, "name"),
                            Rank = role.TryGetProperty("rank", out var r)
                                && r.TryGetInt32(out var rk) ? rk : 0,
                        });
                    }
                    cursor = OptString(root, "nextPageCursor");
                } while (!string.IsNullOrEmpty(cursor));
                list.Sort((a, b) => b.MemberCount.CompareTo(a.MemberCount));
                return list;
            }, "groups");

        // --- batch ID -> names (chunk 100) -----------------------------------
        public Task<Dictionary<long, NameRecord>> GetNamesAsync(IEnumerable<long> ids) =>
            WithRetry(async () =>
            {
                var result = new Dictionary<long, NameRecord>();
                var list = ids.Distinct().ToList();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                for (int i = 0; i < list.Count; i += 100)
                {
                    var chunk = list.Skip(i).Take(100).ToList();
                    var body = JsonSerializer.Serialize(new { userIds = chunk });
                    var resp = await _http.PostAsync("https://users.roblox.com/v1/users",
                        new StringContent(body, Encoding.UTF8, "application/json"));
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        long id = u.GetProperty("id").GetInt64();
                        result[id] = new NameRecord
                        {
                            Id = id,
                            Username = OptString(u, "name"),
                            DisplayName = OptString(u, "displayName"),
                            Verified = u.TryGetProperty("hasVerifiedBadge", out var vb) && vb.ValueKind == JsonValueKind.True,
                            ResolvedAtUnix = now
                        };
                    }
                }
                return result;
            }, "name lookup");

        // --- presence (chunk 100) --------------------------------------------
        public Task<List<PresenceInfo>> GetPresenceAsync(IEnumerable<long> ids) =>
            WithRetry(async () =>
            {
                var result = new List<PresenceInfo>();
                var list = ids.Distinct().ToList();
                for (int i = 0; i < list.Count; i += 100)
                {
                    var chunk = list.Skip(i).Take(100).ToList();
                    var body = JsonSerializer.Serialize(new { userIds = chunk });
                    var resp = await _http.PostAsync("https://presence.roblox.com/v1/presence/users",
                        new StringContent(body, Encoding.UTF8, "application/json"));
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    foreach (var p in doc.RootElement.GetProperty("userPresences").EnumerateArray())
                    {
                        result.Add(new PresenceInfo
                        {
                            UserId = p.GetProperty("userId").GetInt64(),
                            Type = p.GetProperty("userPresenceType").GetInt32(),
                            LastLocation = OptString(p, "lastLocation"),
                            PlaceId = OptLong(p, "placeId"),
                            RootPlaceId = OptLong(p, "rootPlaceId"),
                            GameId = OptString(p, "gameId"),
                            UniverseId = OptLong(p, "universeId")
                        });
                    }
                }
                return result;
            }, "presence");

        // --- avatar headshot URLs (chunk 100) ---------------------------------
        public Task<Dictionary<long, string>> GetAvatarUrlsAsync(IEnumerable<long> ids) =>
            WithRetry(async () =>
            {
                var result = new Dictionary<long, string>();
                var list = ids.Distinct().ToList();
                for (int i = 0; i < list.Count; i += 100)
                {
                    string q = string.Join(",", list.Skip(i).Take(100));
                    var resp = await _http.GetAsync(
                        "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + q +
                        "&size=150x150&format=Png&isCircular=false");
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    foreach (var t in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        if (OptString(t, "state") == "Completed")
                            result[t.GetProperty("targetId").GetInt64()] = OptString(t, "imageUrl");
                    }
                }
                return result;
            }, "avatars");

        // --- game metadata (chunk 50; non-fatal if it fails) -------------------
        public async Task<Dictionary<long, string>> GetGameNamesAsync(IEnumerable<long> universeIds)
        {
            var result = new Dictionary<long, string>();
            try
            {
                var list = universeIds.Distinct().Where(u => u > 0).ToList();
                for (int i = 0; i < list.Count; i += 50)
                {
                    string q = string.Join(",", list.Skip(i).Take(50));
                    var resp = await _http.GetAsync("https://games.roblox.com/v1/games?universeIds=" + q);
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    foreach (var g in doc.RootElement.GetProperty("data").EnumerateArray())
                        result[g.GetProperty("id").GetInt64()] = OptString(g, "name");
                }
            }
            catch { /* non-fatal: caller falls back to lastLocation text */ }
            return result;
        }

        public async Task<byte[]> DownloadBytesAsync(string url)
        {
            var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }
    }
}
