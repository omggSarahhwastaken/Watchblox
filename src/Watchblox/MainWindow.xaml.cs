using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Watchblox.Models;
using Watchblox.Services;
using WbActivityEvent = Watchblox.Models.ActivityEvent;

namespace Watchblox
{
    public partial class MainWindow : Window
    {
        private SettingsService _settings;
        private RobloxApiClient _api;
        private NameCache _names;
        private ThumbnailCache _thumbs;
        private ActivityStore _activity;
        private PresenceMonitor _monitor;
        private UpdateService _updates;
        private MutualService _mutuals;
        private DispatcherTimer _pollTimer;
        private DispatcherTimer _syncTimer;
        private bool _polling;
        private bool _sweeping;
        private bool _initialized;

        private readonly Dictionary<long, FriendEntry> _entries = new Dictionary<long, FriendEntry>();
        private readonly Dictionary<long, string> _gameNames = new Dictionary<long, string>();
        private readonly ObservableCollection<FriendEntry> _friendsView = new ObservableCollection<FriendEntry>();
        private readonly ObservableCollection<FriendEntry> _watchView = new ObservableCollection<FriendEntry>();
        private readonly ObservableCollection<FriendEntry> _mutualView = new ObservableCollection<FriendEntry>();
        private readonly ObservableCollection<WbActivityEvent> _activityView = new ObservableCollection<WbActivityEvent>();

        private string _filter = "All";
        private string _search = "";
        private ResolvedUser _pendingOnboard;
        private UpdateInfo _pendingUpdate;
        private bool _isDownloadingUpdate;
        private System.Windows.Forms.NotifyIcon _tray;
        private bool _reallyExit;
        private string _trackedDisplayName = "";

        public MainWindow()
        {
            InitializeComponent();
            Closing += MainWindow_Closing;

            VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

            _api = new RobloxApiClient();
            _api.StatusChanged += msg => SetStatus(msg);
            _api.SessionExpired += OnSessionExpired;
            _mutuals = new MutualService(_api);
            _settings = new SettingsService();
            _settings.Load();
            _names = new NameCache();
            _thumbs = new ThumbnailCache(_api);
            _activity = new ActivityStore();
            _monitor = new PresenceMonitor();
            _updates = new UpdateService();
            _monitor.Transitions += OnTransitions;

            FriendsList.ItemsSource = _friendsView;
            WatchListBox.ItemsSource = _watchView;
            ActivityList.ItemsSource = _activityView;
            MutualListBox.ItemsSource = _mutualView;

            if (_settings.Settings.HasCompletedOnboarding && _settings.Settings.TrackedUserId != null)
                InitMain();
            else
                OnboardingView.Visibility = Visibility.Visible;
        }

        // ================= onboarding =================

        private async void OnboardFind_Click(object sender, RoutedEventArgs e)
        {
            string username = OnboardUsername.Text.Trim();
            if (string.IsNullOrEmpty(username))
            {
                ShowOnboardError("Type a Roblox username first.");
                return;
            }
            OnboardError.Visibility = Visibility.Collapsed;
            OnboardResult.Visibility = Visibility.Collapsed;
            OnboardFindBtn.IsEnabled = false;
            try
            {
                _pendingOnboard = await _api.ResolveUsernameAsync(username);
                if (_pendingOnboard == null)
                {
                    ShowOnboardError("No Roblox user found with that name.");
                    return;
                }
                var rec = new NameRecord
                {
                    Id = _pendingOnboard.Id,
                    Username = _pendingOnboard.Username,
                    DisplayName = _pendingOnboard.DisplayName,
                    Verified = _pendingOnboard.Verified,
                    ResolvedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                _names.Put(rec);
                _names.Save();

                OnboardDisplayName.Text = rec.DisplayName;
                OnboardUsernameLabel.Text = "@" + rec.Username;
                OnboardVerified.Visibility = rec.Verified ? Visibility.Visible : Visibility.Collapsed;
                OnboardAvatar.Source = null;
                OnboardResult.Visibility = Visibility.Visible;
                _ = LoadAvatarIntoAsync(rec.Id, url =>
                {
                    OnboardAvatar.Source = url != null ? new BitmapImage(new Uri(url)) : null;
                });
            }
            catch (Exception ex)
            {
                ShowOnboardError(ex.Message);
            }
            finally { OnboardFindBtn.IsEnabled = true; }
        }

        private void ShowOnboardError(string msg)
        {
            OnboardError.Text = msg;
            OnboardError.Visibility = Visibility.Visible;
        }

        private void OnboardUsername_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) OnboardFind_Click(sender, e);
        }

        private void OnboardStart_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingOnboard == null) return;
            _settings.Settings.TrackedUserId = _pendingOnboard.Id;
            _settings.Settings.TrackedUsername = _pendingOnboard.Username;
            _settings.Settings.HasCompletedOnboarding = true;
            _settings.Save();
            InitMain();
        }

        // ================= main init =================

        private void InitMain()
        {
            if (_initialized) return;
            _initialized = true;
            OnboardingView.Visibility = Visibility.Collapsed;
            MainView.Visibility = Visibility.Visible;

            var trec = _names.Get(_settings.Settings.TrackedUserId.Value);
            _trackedDisplayName = trec?.DisplayName ?? _settings.Settings.TrackedUsername;
            SideUserName.Text = _trackedDisplayName;
            _ = LoadAvatarIntoAsync(_settings.Settings.TrackedUserId.Value, url =>
            {
                if (url != null)
                {
                    SideAvatar.Source = new BitmapImage(new Uri(url));
                    SideAvatar.Visibility = Visibility.Visible;
                    SideAvatarLetter.Visibility = Visibility.Collapsed;
                }
            });

            // activity history
            foreach (var ev in _activity.Events) _activityView.Add(ev);
            ActivityEmpty.Visibility = _activityView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplySettingsToUi();
            SetupTray();
            FilterChip_Click(ChipAll, new RoutedEventArgs());
            _ = RestoreSessionAsync();

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.Settings.PollIntervalSeconds) };
            _pollTimer.Tick += async (s, e) => await PollPresenceAsync();
            _pollTimer.Start();

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _syncTimer.Tick += async (s, e) => await SyncFriendsAsync();
            _syncTimer.Start();

            SetStatus("Starting…");
            _ = InitDataAsync();
            if (_settings.Settings.CheckForUpdatesOnLaunch)
                _ = CheckForUpdatesAndMaybeInstallAsync();
        }

        // Runs on the UI synchronization context: every await continuation
        // comes back to the UI thread, so bound collections are only ever
        // touched where WPF expects them.
        private async Task InitDataAsync()
        {
            await SyncFriendsAsync();
            await PollPresenceAsync();
        }

        private async Task CheckForUpdatesAndMaybeInstallAsync()
        {
            await CheckUpdatesAsync(silent: true);
            if (_pendingUpdate != null && _settings.Settings.AutoInstallUpdates && !_isDownloadingUpdate)
            {
                UpdateStatus.Text = "Installing update…";
                await InstallUpdateAsync(_pendingUpdate);
            }
        }

        private async Task LoadAvatarIntoAsync(long userId, Action<string> set)
        {
            try
            {
                var urls = await _api.GetAvatarUrlsAsync(new[] { userId });
                urls.TryGetValue(userId, out string url);
                string path = await _thumbs.EnsureAsync(userId, url);
                Dispatcher.Invoke(() => set(string.IsNullOrEmpty(path) ? null : path));
            }
            catch { Dispatcher.Invoke(() => set(null)); }
        }

        private static string AvatarLetter(string name) =>
            string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();

        // ================= friends sync =================

        private async Task SyncFriendsAsync()
        {
            if (_settings.Settings.TrackedUserId == null) return;
            SetStatus("Syncing friends…");
            List<long> ids;
            try
            {
                ids = await _api.GetFriendIdsAsync(_settings.Settings.TrackedUserId.Value);
            }
            catch (Exception ex)
            {
                SetStatus("Friend sync failed: " + ex.Message);
                return;
            }

            // drop entries that are no longer friends (but keep watchlist)
            var friendSet = new HashSet<long>(ids);
            foreach (var id in _entries.Keys.ToList())
                if (!_settings.Settings.WatchlistIds.Contains(id) && !friendSet.Contains(id))
                {
                    _entries.Remove(id);
                    _monitor.Forget(id);
                }

            var allIds = ids.Concat(_settings.Settings.WatchlistIds).Distinct().ToList();
            await ResolveNamesAsync(allIds);
            await EnsureEntriesAsync(allIds, friendSet);
            UpdateSidebarCounts();
            PopulateMutualPicker();
            SetStatus($"Synced {ids.Count} friends.");
            _ = PrefetchAvatarsAsync(allIds);
        }

        private async Task ResolveNamesAsync(List<long> ids)
        {
            var missing = _names.MissingOrStale(ids);
            if (missing.Count == 0) return;
            try
            {
                var recs = await _api.GetNamesAsync(missing);
                foreach (var r in recs.Values) _names.Put(r);
                _names.Save();
            }
            catch { /* names stay unknown; retry next sync */ }
        }

        private Task EnsureEntriesAsync(List<long> ids, HashSet<long> friendSet)
        {
            foreach (var id in ids)
            {
                if (!_entries.TryGetValue(id, out var entry))
                {
                    var rec = _names.Get(id);
                    entry = new FriendEntry
                    {
                        UserId = id,
                        Username = rec?.Username ?? ("user" + id),
                        DisplayName = rec?.DisplayName ?? ("user" + id),
                        Verified = rec?.Verified ?? false,
                    };
                    _entries[id] = entry;
                }
                // Friends display on the Friends page; watchlist is for non-friends.
                entry.IsWatchlist = _settings.Settings.WatchlistIds.Contains(id) && !friendSet.Contains(id);
            }
            return Task.CompletedTask;
        }

        private async Task PrefetchAvatarsAsync(List<long> ids)
        {
            try
            {
                var urls = await _api.GetAvatarUrlsAsync(ids);
                foreach (var kv in urls)
                {
                    string path = await _thumbs.EnsureAsync(kv.Key, kv.Value);
                    if (!string.IsNullOrEmpty(path) && _entries.TryGetValue(kv.Key, out var entry))
                        Dispatcher.Invoke(() => entry.AvatarPath = path);
                }
            }
            catch { }
        }

        private void UpdateSidebarCounts()
        {
            var friends = _entries.Values.Where(e => !e.IsWatchlist).ToList();
            int online = friends.Count(e =>
                e.Status == PresenceType.Online || e.Status == PresenceType.InGame);
            SideUserSub.Text = "@" + _settings.Settings.TrackedUsername +
                $" · {friends.Count} friends · {online} online";
        }

        // ================= presence =================

        private async Task PollPresenceAsync()
        {
            if (_polling) return;
            _polling = true;
            try
            {
                var ids = _entries.Keys.ToList();
                if (ids.Count == 0) { SetStatus("No friends to watch yet."); return; }
                await _monitor.PollAsync(_api, ids);
                await RefreshFromPresenceAsync();
                _ = SweepMutualsAsync();
                int online = _entries.Values.Count(e => e.Status == PresenceType.Online || e.Status == PresenceType.InGame);
                SetStatus($"Updated {DateTime.Now:t} · {online} online");
            }
            catch (Exception ex)
            {
                SetStatus("Presence error: " + ex.Message);
            }
            finally { _polling = false; }
        }

        private async Task RefreshFromPresenceAsync()
        {
            // resolve game names for anyone in-game
            var universeIds = _monitor.Last.Values
                .Where(p => p.Type == 2 && p.UniverseId.HasValue)
                .Select(p => p.UniverseId.Value)
                .Distinct()
                .Where(id => !_gameNames.ContainsKey(id))
                .ToList();
            if (universeIds.Count > 0)
            {
                try
                {
                    var names = await _api.GetGameNamesAsync(universeIds);
                    foreach (var kv in names) _gameNames[kv.Key] = kv.Value;
                }
                catch { }
            }

            foreach (var kv in _monitor.Last)
            {
                if (!_entries.TryGetValue(kv.Key, out var entry)) continue;
                var p = kv.Value;
                entry.Status = p.Type >= 0 && p.Type <= 3 ? (PresenceType)p.Type : PresenceType.Offline;
                string game = p.UniverseId.HasValue && _gameNames.TryGetValue(p.UniverseId.Value, out var gn)
                    ? gn : (p.LastLocation ?? "");
                entry.GameName = game;
                entry.PlaceId = p.PlaceId;
                entry.GameServerId = p.GameId ?? "";
            }
            RefreshDisplay();
        }

        private void OnTransitions(List<PresenceTransition> transitions)
        {
            Dispatcher.Invoke(async () =>
            {
                try
                {
                    // resolve names first so toasts/activity use real names
                    var unknown = transitions.Select(t => t.UserId)
                        .Where(id => _names.Get(id) == null).ToList();
                    if (unknown.Count > 0) await ResolveNamesAsync(unknown);

                    foreach (var t in transitions)
                    {
                        var rec = _names.Get(t.UserId);
                        string name = rec?.DisplayName ?? ("user" + t.UserId);
                        var ev = new WbActivityEvent
                        {
                            UserId = t.UserId,
                            DisplayName = name,
                            Kind = t.Kind,
                            Text = t.Describe(name),
                            Time = t.Time
                        };
                        _activity.Add(ev);
                        _activityView.Insert(0, ev);
                        ActivityEmpty.Visibility = Visibility.Collapsed;
                        if (_settings.Settings.ToastsEnabled)
                            ToastService.Show(name, ev.Text);
                    }
                }
                catch { /* activity/toast path is best-effort; never crash the app */ }
            });
        }

        // ================= display =================

        private void RefreshDisplay()
        {
            if (!_initialized) return;
            var friends = _entries.Values
                .Where(e => !e.IsWatchlist)
                .Where(e => MatchesFilter(e) && MatchesSearch(e))
                .OrderByDescending(e => e.Status == PresenceType.InGame)
                .ThenByDescending(e => e.Status == PresenceType.Online)
                .ThenBy(e => e.DisplayName)
                .ToList();
            _friendsView.Clear();
            foreach (var f in friends) _friendsView.Add(f);
            FriendsEmpty.Visibility = friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var watch = _entries.Values
                .Where(e => e.IsWatchlist)
                .OrderByDescending(e => e.Status == PresenceType.InGame)
                .ThenBy(e => e.DisplayName)
                .ToList();
            _watchView.Clear();
            foreach (var w in watch) _watchView.Add(w);
            UpdateSidebarCounts();
        }

        private bool MatchesFilter(FriendEntry e) => _filter switch
        {
            "Online" => e.Status == PresenceType.Online,
            "InGame" => e.Status == PresenceType.InGame,
            "Offline" => e.Status == PresenceType.Offline,
            _ => true
        };

        private bool MatchesSearch(FriendEntry e)
        {
            if (string.IsNullOrWhiteSpace(_search)) return true;
            string s = _search.Trim().ToLowerInvariant();
            return e.DisplayName.ToLowerInvariant().Contains(s)
                || e.Username.ToLowerInvariant().Contains(s);
        }

        private void FilterChip_Click(object sender, RoutedEventArgs e)
        {
            _filter = (sender as Button)?.Tag as string ?? "All";
            foreach (var chip in new[] { ChipAll, ChipOnline, ChipInGame, ChipOffline })
            {
                bool active = (chip.Tag as string) == _filter;
                chip.Background = active
                    ? (Brush)FindResource("AccentBrush")
                    : (Brush)FindResource("Surface2Brush");
                chip.Foreground = active ? Brushes.White : (Brush)FindResource("TextBrush");
            }
            RefreshDisplay();
        }

        private void FriendSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _search = FriendSearch.Text;
            RefreshDisplay();
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            foreach (var b in new[] { NavFriends, NavActivity, NavWatchlist, NavMutuals, NavSettings })
                b.Tag = "";
            btn.Tag = "Active";
            FriendsPage.Visibility = btn == NavFriends ? Visibility.Visible : Visibility.Collapsed;
            ActivityPage.Visibility = btn == NavActivity ? Visibility.Visible : Visibility.Collapsed;
            WatchlistPage.Visibility = btn == NavWatchlist ? Visibility.Visible : Visibility.Collapsed;
            MutualsPage.Visibility = btn == NavMutuals ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = btn == NavSettings ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearActivity_Click(object sender, RoutedEventArgs e)
        {
            _activity.Clear();
            _activityView.Clear();
            ActivityEmpty.Visibility = Visibility.Visible;
        }

        // ================= join =================

        private void JoinButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = (sender as Button)?.Tag as FriendEntry;
            if (entry == null || !entry.PlaceId.HasValue) return;
            string uri = "roblox://placeId=" + entry.PlaceId.Value;
            if (!string.IsNullOrEmpty(entry.GameServerId))
                uri += "&gameInstanceId=" + entry.GameServerId;
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch
            {
                // Exact-server launch failed. Watchblox never falls back to a
                // browser — report it honestly in the app instead.
                string msg = "Couldn't launch Roblox into that server.";
                SetStatus(msg);
                ToastService.Show("Watchblox", msg);
            }
        }

        // ================= watchlist =================

        private async void WatchAdd_Click(object sender, RoutedEventArgs e)
        {
            string username = WatchUsername.Text.Trim();
            if (string.IsNullOrEmpty(username)) return;
            WatchError.Visibility = Visibility.Collapsed;
            try
            {
                var lookup = await _api.ResolveUsernameAsync(username);
                if (lookup == null)
                {
                    WatchError.Text = "No Roblox user found with that name.";
                    WatchError.Visibility = Visibility.Visible;
                    return;
                }
                if (_settings.Settings.WatchlistIds.Contains(lookup.Id))
                {
                    WatchError.Text = "Already on your watchlist.";
                    WatchError.Visibility = Visibility.Visible;
                    return;
                }
                if (_entries.TryGetValue(lookup.Id, out var existing) && !existing.IsWatchlist)
                {
                    WatchError.Text = "They're already a friend — find them on the Friends page.";
                    WatchError.Visibility = Visibility.Visible;
                    return;
                }
                _settings.Settings.WatchlistIds.Add(lookup.Id);
                _settings.Save();
                var rec = new NameRecord
                {
                    Id = lookup.Id, Username = lookup.Username, DisplayName = lookup.DisplayName,
                    Verified = lookup.Verified, ResolvedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                _names.Put(rec);
                _names.Save();
                var entry = new FriendEntry
                {
                    UserId = lookup.Id, Username = lookup.Username, DisplayName = lookup.DisplayName,
                    Verified = lookup.Verified, IsWatchlist = true
                };
                _entries[lookup.Id] = entry;
                WatchUsername.Text = "";
                RefreshDisplay();
                _ = PollPresenceAsync();
                _ = PrefetchAvatarsAsync(new List<long> { lookup.Id });
            }
            catch (Exception ex)
            {
                WatchError.Text = ex.Message;
                WatchError.Visibility = Visibility.Visible;
            }
        }

        private void WatchUsername_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) WatchAdd_Click(sender, e);
        }

        private void RemoveWatch_Click(object sender, RoutedEventArgs e)
        {
            var entry = (sender as Button)?.Tag as FriendEntry;
            if (entry == null) return;
            _settings.Settings.WatchlistIds.Remove(entry.UserId);
            _settings.Save();
            _entries.Remove(entry.UserId);
            _monitor.Forget(entry.UserId);
            RefreshDisplay();
        }

        // ================= settings =================

        private void ApplySettingsToUi()
        {
            var s = _settings.Settings;
            foreach (ComboBoxItem item in PollCombo.Items)
                if ((item.Tag as string) == s.PollIntervalSeconds.ToString())
                    PollCombo.SelectedItem = item;
            ToastCheck.IsChecked = s.ToastsEnabled;
            StartWinCheck.IsChecked = s.StartWithWindows;
            UpdateCheckBox.IsChecked = s.CheckForUpdatesOnLaunch;
            AutoUpdateCheckBox.IsChecked = s.AutoInstallUpdates;
            TrayCheck.IsChecked = s.MinimizeToTray;
            ScaleSlider.Value = s.InterfaceScale * 100;
            ScaleLabel.Text = ((int)(s.InterfaceScale * 100)) + "%";
            ApplyScale(s.InterfaceScale);
            TrackedLabel.Text = "@" + s.TrackedUsername + "  (" + _trackedDisplayName + ")";
            if (_settings.ConfigBroken)
                SetStatus("Settings file was unreadable — loaded backup/defaults.");
        }

        private void PollCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || PollCombo.SelectedItem is not ComboBoxItem item) return;
            if (int.TryParse(item.Tag as string, out int secs))
            {
                _settings.Settings.PollIntervalSeconds = secs;
                _settings.Save();
                if (_pollTimer != null) _pollTimer.Interval = TimeSpan.FromSeconds(secs);
            }
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            var s = _settings.Settings;
            s.ToastsEnabled = ToastCheck.IsChecked == true;
            s.StartWithWindows = StartWinCheck.IsChecked == true;
            s.CheckForUpdatesOnLaunch = UpdateCheckBox.IsChecked == true;
            s.AutoInstallUpdates = AutoUpdateCheckBox.IsChecked == true;
            s.MinimizeToTray = TrayCheck.IsChecked == true;
            if (!_settings.Save())
                SetStatus("Couldn't save settings — the change may not stick after restart.");
            SetStartupRegistration(s.StartWithWindows);
        }

        private void ScaleSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_initialized) return;
            int pct = (int)ScaleSlider.Value;
            ScaleLabel.Text = pct + "%";
            double frac = pct / 100.0;
            ApplyScale(frac);
            _settings.Settings.InterfaceScale = frac;
            _settings.Save();
        }

        private void ApplyScale(double fraction)
        {
            RootGrid.LayoutTransform = new ScaleTransform(fraction, fraction);
        }

        private void SetStartupRegistration(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enable)
                        key.SetValue("Watchblox", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else
                        key.DeleteValue("Watchblox", false);
                }
            }
            catch { }
        }

        private void SwitchAccount_Click(object sender, RoutedEventArgs e)
        {
            _settings.Settings.HasCompletedOnboarding = false;
            _settings.Save();
            _pollTimer?.Stop();
            _syncTimer?.Stop();
            _initialized = false;
            _entries.Clear();
            _friendsView.Clear();
            _watchView.Clear();
            _mutualView.Clear();
            _mutuals.Clear();
            MutualPicker.Items.Clear();
            MutualHeader.Text = "";
            MutualEmpty.Text = "Pick a friend above to see who you both know.";
            MutualEmpty.Visibility = Visibility.Visible;
            MainView.Visibility = Visibility.Collapsed;
            OnboardingView.Visibility = Visibility.Visible;
            OnboardResult.Visibility = Visibility.Collapsed;
            OnboardUsername.Text = "";
        }

        // ================= login =================

        private void UpdateLoginUi(bool loggedIn, string username)
        {
            LoginForm.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
            LogoutForm.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
            if (loggedIn) LoggedInLabel.Text = "Logged in as @" + username;
        }

        private async Task RestoreSessionAsync()
        {
            string blob = _settings.Settings.EncryptedCookie;
            if (string.IsNullOrEmpty(blob)) { UpdateLoginUi(false, ""); return; }
            try
            {
                _api.SetSessionCookie(CookieVault.Unprotect(blob));
                var me = await _api.GetAuthenticatedUserAsync();
                UpdateLoginUi(true, me.Username);
                SetStatus("Logged in as @" + me.Username + ".");
            }
            catch
            {
                // Bad or expired blob: drop it and stay logged out.
                _api.ClearSessionCookie();
                _settings.Settings.EncryptedCookie = "";
                _settings.Save();
                UpdateLoginUi(false, "");
            }
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            string cookie = CookieBox.Password.Trim();
            CookieBox.Clear();
            if (string.IsNullOrEmpty(cookie))
            {
                LoginStatus.Text = "Paste your .ROBLOSECURITY cookie first.";
                return;
            }
            LoginStatus.Text = "Verifying…";
            try
            {
                _api.SetSessionCookie(cookie);
                var me = await _api.GetAuthenticatedUserAsync();
                _settings.Settings.EncryptedCookie = CookieVault.Protect(cookie);
                _settings.Save();
                UpdateLoginUi(true, me.Username);
                LoginStatus.Text = "";
                SetStatus("Logged in as @" + me.Username + " — presence is now friend-accurate.");
                _ = PollPresenceAsync(); // re-poll with the session: statuses sharpen up
            }
            catch (Exception ex)
            {
                _api.ClearSessionCookie();
                LoginStatus.Text = ex.Message;
            }
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            _api.ClearSessionCookie();
            _settings.Settings.EncryptedCookie = "";
            _settings.Save();
            _mutuals.Clear();
            foreach (var entry in _entries.Values) entry.MutualCount = -1;
            _mutualView.Clear();
            MutualHeader.Text = "";
            MutualEmpty.Text = "Pick a friend above to see who you both know.";
            MutualEmpty.Visibility = Visibility.Visible;
            LoginStatus.Text = "";
            UpdateLoginUi(false, "");
            RefreshDisplay();
            SetStatus("Logged out.");
        }

        private void OnSessionExpired()
        {
            Dispatcher.Invoke(() =>
            {
                _settings.Settings.EncryptedCookie = "";
                _settings.Save();
                UpdateLoginUi(false, "");
                SetStatus("Roblox session expired — log in again.");
                ToastService.Show("Watchblox", "Roblox session expired — log in again.");
            });
        }

        // ================= mutuals =================

        private void PopulateMutualPicker()
        {
            if (!_initialized) return;
            long? selected = (MutualPicker.SelectedItem as ComboBoxItem)?.Tag as long?;
            MutualPicker.SelectionChanged -= MutualPicker_Changed;
            MutualPicker.Items.Clear();
            foreach (var e in _entries.Values.Where(x => !x.IsWatchlist).OrderBy(x => x.DisplayName))
            {
                var item = new ComboBoxItem
                {
                    Content = e.DisplayName + " (@" + e.Username + ")",
                    Tag = e.UserId
                };
                MutualPicker.Items.Add(item);
                if (selected == e.UserId) MutualPicker.SelectedItem = item;
            }
            MutualPicker.SelectionChanged += MutualPicker_Changed;
        }

        private void MutualPicker_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || MutualPicker.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is long userId) _ = LoadMutualsAsync(userId);
        }

        private async Task LoadMutualsAsync(long userId)
        {
            _mutualView.Clear();
            MutualEmpty.Visibility = Visibility.Collapsed;
            MutualHeader.Text = "Loading…";
            try
            {
                var myIds = new HashSet<long>(
                    _entries.Values.Where(x => !x.IsWatchlist).Select(x => x.UserId));
                var mutualIds = await _mutuals.GetMutualIdsAsync(userId, myIds);
                await ResolveNamesAsync(mutualIds);
                if (_entries.TryGetValue(userId, out var picked)) picked.MutualCount = mutualIds.Count;
                foreach (var id in mutualIds)
                {
                    var rec = _names.Get(id);
                    _mutualView.Add(new FriendEntry
                    {
                        UserId = id,
                        Username = rec?.Username ?? ("user" + id),
                        DisplayName = rec?.DisplayName ?? ("user" + id),
                        Verified = rec?.Verified ?? false
                    });
                }
                MutualHeader.Text = mutualIds.Count == 1 ? "1 mutual friend" : $"{mutualIds.Count} mutual friends";
                if (mutualIds.Count == 0)
                {
                    MutualEmpty.Text = "No mutual friends found.";
                    MutualEmpty.Visibility = Visibility.Visible;
                }
                _ = PrefetchAvatarsAsync(mutualIds);
            }
            catch (Exception ex)
            {
                MutualHeader.Text = "";
                MutualEmpty.Text = "Couldn't load mutuals: " + ex.Message;
                MutualEmpty.Visibility = Visibility.Visible;
            }
        }

        // Staggered background sweep: a couple of friends per presence poll,
        // so mutual badges on the Friends page warm up over time without
        // hammering the API.
        private async Task SweepMutualsAsync()
        {
            if (_sweeping) return;
            _sweeping = true;
            try
            {
                var myIds = new HashSet<long>(
                    _entries.Values.Where(x => !x.IsWatchlist).Select(x => x.UserId));
                var targets = _entries.Values
                    .Where(x => !x.IsWatchlist && x.MutualCount < 0)
                    .Take(2).ToList();
                foreach (var t in targets)
                {
                    try
                    {
                        var mutual = await _mutuals.GetMutualIdsAsync(t.UserId, myIds);
                        t.MutualCount = mutual.Count;
                    }
                    catch { /* try again next cycle */ }
                }
            }
            finally { _sweeping = false; }
        }

        // ================= updates =================

        private async Task CheckUpdatesAsync(bool silent)
        {
            if (!silent) UpdateStatus.Text = "Checking…";
            var info = await _updates.CheckForUpdatesAsync();
            if (info.Available)
            {
                _pendingUpdate = info;
                UpdateButton.Visibility = Visibility.Visible;
                UpdateStatus.Text = $"v{info.Version} available (you have v{info.LocalVersion}).";
                if (!string.IsNullOrEmpty(info.Notes))
                    UpdateStatus.Text += " " + info.Notes;
            }
            else
            {
                _pendingUpdate = null;
                UpdateButton.Visibility = Visibility.Collapsed;
                UpdateStatus.Text = info.Message;
                if (!silent && !string.IsNullOrEmpty(info.Message))
                    ToastService.Show("Watchblox", info.Message);
            }
        }

        private void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
            _ = CheckUpdatesAsync(silent: false);

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate == null || _isDownloadingUpdate) return;
            _ = InstallUpdateAsync(_pendingUpdate);
        }

        private async Task InstallUpdateAsync(UpdateInfo info)
        {
            _isDownloadingUpdate = true;
            UpdateButton.IsEnabled = false;
            UpdateButton.Content = "Downloading…";
            bool ok = await _updates.DownloadAndInstallAsync(info);
            if (ok)
            {
                _reallyExit = true;
                Application.Current.Shutdown();
            }
            else
            {
                _isDownloadingUpdate = false;
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = "Update available — install";
                UpdateStatus.Text = "Update download failed — will try again later.";
            }
        }

        // ================= tray =================

        private void SetupTray()
        {
            try
            {
                string iconPath = Path.Combine(
                    AppContext.BaseDirectory, "app.ico");
                _tray = new System.Windows.Forms.NotifyIcon
                {
                    Text = "Watchblox",
                    Visible = false
                };
                if (File.Exists(iconPath))
                    _tray.Icon = new System.Drawing.Icon(iconPath);
                _tray.DoubleClick += (s, e) => RestoreFromTray();
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open", null, (s, e) => RestoreFromTray());
                menu.Items.Add("Exit", null, (s, e) => { _reallyExit = true; Close(); });
                _tray.ContextMenuStrip = menu;
            }
            catch { }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_reallyExit && _initialized && _settings.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                if (_tray != null) _tray.Visible = true;
            }
            else
            {
                // Really exiting: flush settings so a toggle changed moments
                // before close can never be lost.
                _settings.Save();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            }
        }

        internal void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_tray != null) _tray.Visible = false;
        }

        private void SetStatus(string msg)
        {
            // Best-effort: the dispatcher may be shutting down (e.g. during
            // an automatic update) when a background continuation reports in.
            try { Dispatcher.Invoke(() => StatusText.Text = msg); } catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _api?.Dispose();
            base.OnClosed(e);
        }
    }
}
