using System;
using System.ComponentModel;
using System.Windows.Media;

namespace Watchblox.Models
{
    public enum PresenceType
    {
        Offline = 0,
        Online = 1,
        InGame = 2,
        InStudio = 3
    }

    public class FriendEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public long UserId { get; set; }

        private string _displayName = "";
        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; Raise(nameof(DisplayName)); Raise(nameof(AvatarLetter)); }
        }

        private string _username = "";
        public string Username
        {
            get => _username;
            set { _username = value; Raise(nameof(Username)); }
        }

        public bool Verified { get; set; }
        public bool IsWatchlist { get; set; }

        private string _avatarPath = "";
        public string AvatarPath
        {
            get => _avatarPath;
            set { _avatarPath = value; Raise(nameof(AvatarPath)); Raise(nameof(HasAvatar)); }
        }
        public bool HasAvatar => !string.IsNullOrEmpty(_avatarPath);
        public string AvatarLetter => string.IsNullOrEmpty(_displayName) ? "?" : _displayName.Substring(0, 1).ToUpperInvariant();

        private PresenceType _status = PresenceType.Offline;
        public PresenceType Status
        {
            get => _status;
            set
            {
                _status = value;
                Raise(nameof(Status));
                Raise(nameof(StatusText));
                Raise(nameof(StatusColor));
                Raise(nameof(ShowJoin));
                Raise(nameof(GameLine));
                Raise(nameof(GameLineVisible));
            }
        }

        private string _gameName = "";
        public string GameName
        {
            get => _gameName;
            set { _gameName = value; Raise(nameof(GameName)); Raise(nameof(GameLine)); }
        }

        public long? PlaceId { get; set; }
        public string GameServerId { get; set; } = "";

        // Mutual-friend count vs the tracked account. -1 = not computed yet.
        private int _mutualCount = -1;
        public int MutualCount
        {
            get => _mutualCount;
            set
            {
                _mutualCount = value;
                Raise(nameof(MutualCount));
                Raise(nameof(MutualText));
                Raise(nameof(MutualVisible));
            }
        }

        public string MutualText => _mutualCount == 1 ? "1 mutual friend" : $"{_mutualCount} mutual friends";
        public bool MutualVisible => _mutualCount >= 0;

        public string StatusText => _status switch
        {
            PresenceType.Online => "Online",
            PresenceType.InGame => "In Game",
            PresenceType.InStudio => "In Studio",
            _ => "Offline"
        };

        public SolidColorBrush StatusColor => new SolidColorBrush(_status switch
        {
            PresenceType.Online => Color.FromRgb(0x22, 0xc5, 0x5e),
            PresenceType.InGame => Color.FromRgb(0x3b, 0x82, 0xf6),
            PresenceType.InStudio => Color.FromRgb(0xf5, 0x9e, 0x0b),
            _ => Color.FromRgb(0x6b, 0x72, 0x80)
        });

        public bool ShowJoin => _status == PresenceType.InGame
            && PlaceId.HasValue && !string.IsNullOrEmpty(GameServerId);

        public string GameLine => _status == PresenceType.InGame
            ? (string.IsNullOrEmpty(_gameName) ? "In a game" : _gameName)
            : StatusText;

        public bool GameLineVisible => _status == PresenceType.InGame;
    }
}
