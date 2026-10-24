using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Watchblox.Models;
using Watchblox.Services;

namespace Watchblox
{
    public partial class ProfileWindow : Window
    {
        private readonly RobloxApiClient _api;
        private readonly ThumbnailCache _thumbs;
        private readonly Action<FriendEntry> _onJoin;
        private readonly PresenceHistoryService _history;
        private readonly ActivityStore _activity;

        private FriendEntry _entry;
        private long _loadToken;

        public ProfileWindow(RobloxApiClient api, ThumbnailCache thumbs, Action<FriendEntry> onJoin,
            PresenceHistoryService history, ActivityStore activity)
        {
            InitializeComponent();
            _api = api;
            _thumbs = thumbs;
            _onJoin = onJoin;
            _history = history;
            _activity = activity;
            // This window is a single reusable instance: closing it with the
            // X button must hide, not destroy, it. Show() on a closed Window
            // throws InvalidOperationException (crashed the app on the second
            // dot click in v1.4.0).
            Closing += (s, e) => { e.Cancel = true; Hide(); };
        }

        public void ShowProfile(FriendEntry entry, int connections)
        {
            _entry = entry;
            long token = ++_loadToken;

            Title = entry.DisplayName + " — Profile";
            ProfileName.Text = entry.DisplayName;
            ProfileUser.Text = "@" + entry.Username;
            ProfileLetter.Text = entry.AvatarLetter;
            ProfileBadge.Visibility = entry.Verified ? Visibility.Visible : Visibility.Collapsed;
            ProfileStatus.Text = entry.StatusText;
            ProfileStatus.Foreground = entry.StatusColor;
            InfoStatus.Text = entry.StatusText;
            ProfileBio.Text = "Loading…";
            ProfileJoined.Text = "—";
            ProfileId.Text = entry.UserId.ToString();
            ProfileConns.Text = connections < 0 ? "—"
                : connections == 1 ? "1 friend" : $"{connections} friends";
            ProfileGame.Text = entry.Status == PresenceType.InGame && !string.IsNullOrEmpty(entry.GameName)
                ? entry.GameName : "—";
            ProfileJoinBtn.Visibility = entry.ShowJoin ? Visibility.Visible : Visibility.Collapsed;
            ProfileAvatar.Visibility = Visibility.Collapsed;
            ProfileLetter.Visibility = Visibility.Visible;

            // Info-card stats from local history (sync, no network).
            ProfilePeaks.Text = FormatPeaks(entry.UserId);
            ProfileTopGame.Text = "Loading…";
            ProfileLastSeen.Text = FormatLastSeen(entry);

            GroupsLoading.Visibility = Visibility.Visible;
            GroupsEmpty.Visibility = Visibility.Collapsed;
            GroupsList.Visibility = Visibility.Collapsed;
            GroupsList.ItemsSource = null;

            GamesLoading.Visibility = Visibility.Visible;
            GamesEmpty.Visibility = Visibility.Collapsed;
            GamesList.Visibility = Visibility.Collapsed;
            GamesList.ItemsSource = null;

            FavsLoading.Visibility = Visibility.Visible;
            FavsEmpty.Visibility = Visibility.Collapsed;
            FavsList.Visibility = Visibility.Collapsed;
            FavsList.ItemsSource = null;

            AvatarLoading.Visibility = Visibility.Visible;
            AvatarEmpty.Visibility = Visibility.Collapsed;
            AvatarFullImage.Visibility = Visibility.Collapsed;
            AvatarFullImage.Source = null;

            ShowActivity(entry.UserId);

            if (!IsVisible) Show();
            else Activate();

            _ = LoadAvatarAsync(entry.UserId, token);
            _ = LoadProfileAsync(entry.UserId, token);
            _ = LoadGroupsAsync(entry.UserId, token);
            _ = LoadGamesAsync(entry.UserId, token);
            _ = LoadFavoritesAsync(entry.UserId, token);
            _ = LoadFullAvatarAsync(entry.UserId, token);
            _ = LoadTopGameAsync(entry.UserId, token);
        }

        private string FormatPeaks(long userId)
        {
            int samples = _history.TotalSamples(userId);
            if (samples < 40)
                return "Not enough data yet — peaks appear once the app has watched a while.";
            var peaks = _history.PeakHours(userId);
            if (peaks.Count == 0)
                return "No clear peak yet.";
            return string.Join(" · ", peaks.Select(p =>
                $"{PresenceHistoryService.FormatBucket(p.bucket)} ({p.rate:0%})"));
        }

        private string FormatLastSeen(FriendEntry entry)
        {
            if (entry.Status != PresenceType.Offline) return "Online now";
            var last = _history.LastSeenLocal(entry.UserId);
            if (last == null) return "—";
            return PresenceHistoryService.FormatLastSeenShort(last.Value);
        }

        private void ShowActivity(long userId)
        {
            var events = _activity.Events
                .Where(e => e.UserId == userId)
                .Take(30)
                .ToList();
            ActivityList.ItemsSource = events;
            ActivityEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ActivityList.Visibility = events.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task LoadAvatarAsync(long userId, long token)
        {
            try
            {
                var urls = await _api.GetAvatarUrlsAsync(new[] { userId });
                urls.TryGetValue(userId, out string url);
                string path = await _thumbs.EnsureAsync(userId, url);
                if (token != _loadToken || string.IsNullOrEmpty(path)) return;
                Dispatcher.Invoke(() =>
                {
                    if (token != _loadToken) return;
                    ProfileAvatarBrush.ImageSource = new BitmapImage(new Uri(path));
                    ProfileAvatar.Visibility = Visibility.Visible;
                    ProfileLetter.Visibility = Visibility.Collapsed;
                });
            }
            catch { }
        }

        private async Task LoadProfileAsync(long userId, long token)
        {
            try
            {
                var p = await _api.GetUserProfileAsync(userId);
                if (token != _loadToken) return;
                Dispatcher.Invoke(() =>
                {
                    if (token != _loadToken) return;
                    ProfileBio.Text = string.IsNullOrWhiteSpace(p.Description)
                        ? "No bio."
                        : p.Description.Trim();
                    ProfileJoined.Text = p.Created == DateTime.MinValue
                        ? "—"
                        : p.Created.ToString("MMMM d, yyyy");
                    if (p.HasVerifiedBadge)
                        ProfileBadge.Visibility = Visibility.Visible;
                });
            }
            catch
            {
                SafeInvoke(() =>
                {
                    if (token != _loadToken) return;
                    ProfileBio.Text = "Couldn't load profile.";
                });
            }
        }

        private async Task LoadGroupsAsync(long userId, long token)
        {
            try
            {
                var groups = await _api.GetUserGroupsAsync(userId);
                if (token != _loadToken) return;
                Dispatcher.Invoke(() =>
                {
                    if (token != _loadToken) return;
                    GroupsLoading.Visibility = Visibility.Collapsed;
                    if (groups.Count == 0)
                    {
                        GroupsEmpty.Visibility = Visibility.Visible;
                        return;
                    }
                    var rows = new List<GroupRow>();
                    foreach (var g in groups)
                        rows.Add(new GroupRow
                        {
                            GroupName = g.GroupName,
                            RoleName = string.IsNullOrEmpty(g.RoleName) ? "Member" : g.RoleName,
                            MemberCountText = FormatMembers(g.MemberCount)
                        });
                    GroupsList.ItemsSource = rows;
                    GroupsList.Visibility = Visibility.Visible;
                });
            }
            catch
            {
                SafeInvoke(() =>
                {
                    if (token != _loadToken) return;
                    GroupsLoading.Visibility = Visibility.Collapsed;
                    GroupsEmpty.Text = "Couldn't load groups.";
                    GroupsEmpty.Visibility = Visibility.Visible;
                });
            }
        }

        private async Task LoadGamesAsync(long userId, long token)
        {
            try
            {
                var games = await _api.GetUserGamesAsync(userId);
                if (token != _loadToken) return;
                Dispatcher.Invoke(() =>
                {
                    if (token != _loadToken) return;
                    GamesLoading.Visibility = Visibility.Collapsed;
                    if (games.Count == 0)
                    {
                        GamesEmpty.Visibility = Visibility.Visible;
                        return;
                    }
                    GamesList.ItemsSource = games.Select(g => new GameRow(g)).ToList();
                    GamesList.Visibility = Visibility.Visible;
                });
            }
            catch
            {
                SafeInvoke(() =>
                {
                    if (token != _loadToken) return;
                    GamesLoading.Visibility = Visibility.Collapsed;
                    GamesEmpty.Text = "Couldn't load games.";
                    GamesEmpty.Visibility = Visibility.Visible;
                });
            }
        }

        private async Task LoadFavoritesAsync(long userId, long token)
        {
            try
            {
                var games = await _api.GetFavoriteGamesAsync(userId);
                if (token != _loadToken) return;
                Dispatcher.Invoke(() =>
                {
                    if (token != _loadToken) return;
                    FavsLoading.Visibility = Visibility.Collapsed;
                    if (games.Count == 0)
                    {
                        FavsEmpty.Visibility = Visibility.Visible;
                        return;
                    }
                    FavsList.ItemsSource = games.Select(g => new GameRow(g)).ToList();
                    FavsList.Visibility = Visibility.Visible;
                });
            }
            catch
            {
                SafeInvoke(() =>
                {
                    if (token != _loadToken) return;
                    FavsLoading.Visibility = Visibility.Collapsed;
                    FavsEmpty.Text = "Couldn't load favorites.";
                    FavsEmpty.Visibility = Visibility.Visible;
                });
            }
        }

        private async Task LoadFullAvatarAsync(long userId, long token)
        {
            try
            {
                string url = await _api.GetFullBodyAvatarUrlAsync(userId);
                if (token != _loadToken || string.IsNullOrEmpty(url)) throw new Exception("no avatar");
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(url);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                Dispatcher.Invoke(() =>
                {
                    if (token != _loadToken) return;
                    AvatarLoading.Visibility = Visibility.Collapsed;
                    AvatarFullImage.Source = img;
                    AvatarFullImage.Visibility = Visibility.Visible;
                });
            }
            catch
            {
                SafeInvoke(() =>
                {
                    if (token != _loadToken) return;
                    AvatarLoading.Visibility = Visibility.Collapsed;
                    AvatarEmpty.Visibility = Visibility.Visible;
                });
            }
        }

        private async Task LoadTopGameAsync(long userId, long token)
        {
            try
            {
                long? universe = _history.TopGameUniverse(userId);
                if (token != _loadToken) return;
                if (universe == null)
                {
                    SafeInvoke(() =>
                    {
                        if (token != _loadToken) return;
                        ProfileTopGame.Text = "—";
                    });
                    return;
                }
                var names = await _api.GetGameNamesAsync(new[] { universe.Value });
                if (token != _loadToken) return;
                string name = names.TryGetValue(universe.Value, out var n) ? n : "A game";
                SafeInvoke(() =>
                {
                    if (token != _loadToken) return;
                    ProfileTopGame.Text = name;
                });
            }
            catch
            {
                SafeInvoke(() =>
                {
                    if (token != _loadToken) return;
                    ProfileTopGame.Text = "—";
                });
            }
        }

        private static string FormatMembers(int n) =>
            n switch
            {
                >= 1_000_000 => $"{n / 1_000_000.0:0.#}M members",
                >= 1_000 => $"{n / 1_000.0:0.#}K members",
                1 => "1 member",
                _ => $"{n} members"
            };

        private static string FormatVisits(long n) =>
            n switch
            {
                >= 1_000_000 => $"{n / 1_000_000.0:0.#}M visits",
                >= 1_000 => $"{n / 1_000.0:0.#}K visits",
                1 => "1 visit",
                _ => $"{n} visits"
            };

        private void ProfileJoin_Click(object sender, RoutedEventArgs e)
        {
            if (_entry != null) _onJoin(_entry);
        }

        private void ProfileCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_entry == null) return;
            try { Clipboard.SetText($"https://www.roblox.com/users/{_entry.UserId}/profile"); }
            catch { }
        }

        private void GamePlay_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is long placeId && placeId > 0)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("roblox://placeId=" + placeId)
                        { UseShellExecute = true });
                }
                catch { }
            }
        }

        // Dispatcher.Invoke during shutdown throws; the catch blocks below
        // run outside any try, so guard them.
        private void SafeInvoke(Action action)
        {
            try { Dispatcher.Invoke(action); }
            catch { }
        }

        private class GroupRow
        {
            public string GroupName { get; set; } = "";
            public string RoleName { get; set; } = "";
            public string MemberCountText { get; set; } = "";
        }

        private class GameRow
        {
            public GameRow(UserGameInfo g)
            {
                Name = string.IsNullOrEmpty(g.Name) ? "Untitled game" : g.Name;
                PlaceId = g.PlaceId ?? 0;
                ShowPlay = g.PlaceId.HasValue && g.PlaceId.Value > 0;
                ShowVisits = g.PlaceVisits > 0;
                VisitsText = FormatVisits(g.PlaceVisits);
            }

            public string Name { get; set; }
            public long PlaceId { get; set; }
            public bool ShowPlay { get; set; }
            public bool ShowVisits { get; set; }
            public string VisitsText { get; set; }
        }
    }
}
