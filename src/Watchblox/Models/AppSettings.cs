using System.Collections.Generic;

namespace Watchblox.Models
{
    public class AppSettings
    {
        public long? TrackedUserId { get; set; }
        public string TrackedUsername { get; set; } = "";
        public int PollIntervalSeconds { get; set; } = 60;
        public bool ToastsEnabled { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool CheckForUpdatesOnLaunch { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public double InterfaceScale { get; set; } = 1.0;
        public List<long> WatchlistIds { get; set; } = new List<long>();
        public bool HasCompletedOnboarding { get; set; } = false;
    }
}
