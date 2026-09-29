using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
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

        private FriendEntry _entry;
        private long _loadToken;

        public ProfileWindow(RobloxApiClient api, ThumbnailCache thumbs, Action<FriendEntry> onJoin)
        {
            InitializeComponent();
            _api = api;
            _thumbs = thumbs;
            _onJoin = onJoin;
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
            ProfileBio.Text = "Loading…";
            ProfileJoined.Text = "—";
            ProfileId.Text = entry.UserId.ToString();
            ProfileConns.Text = connections == 1 ? "1 friend" : $"{connections} friends";
            ProfileGame.Text = entry.Status == PresenceType.InGame && !string.IsNullOrEmpty(entry.GameName)
                ? entry.GameName : "—";
            ProfileJoinBtn.Visibility = entry.ShowJoin ? Visibility.Visible : Visibility.Collapsed;
            ProfileAvatar.Visibility = Visibility.Collapsed;
            ProfileLetter.Visibility = Visibility.Visible;

            GroupsLoading.Visibility = Visibility.Visible;
            GroupsEmpty.Visibility = Visibility.Collapsed;
            GroupsList.Visibility = Visibility.Collapsed;
            GroupsList.ItemsSource = null;

            if (!IsVisible) Show();
            else Activate();

            _ = LoadAvatarAsync(entry.UserId, token);
            _ = LoadProfileAsync(entry.UserId, token);
            _ = LoadGroupsAsync(entry.UserId, token);
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
                Dispatcher.Invoke(() =>
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
                Dispatcher.Invoke(() =>
                {
                    if (token != _loadToken) return;
                    GroupsLoading.Visibility = Visibility.Collapsed;
                    GroupsEmpty.Text = "Couldn't load groups.";
                    GroupsEmpty.Visibility = Visibility.Visible;
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

        private void ProfileJoin_Click(object sender, RoutedEventArgs e)
        {
            if (_entry != null) _onJoin(_entry);
        }

        private class GroupRow
        {
            public string GroupName { get; set; } = "";
            public string RoleName { get; set; } = "";
            public string MemberCountText { get; set; } = "";
        }
    }
}
