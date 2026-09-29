using System;

namespace Watchblox.Models
{
    public class ActivityEvent
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public long UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Username { get; set; } = "";
        public string Text { get; set; } = "";
        public string Kind { get; set; } = "online"; // online, offline, game, studio
        public string AvatarPath { get; set; } = "";
        public string TimeText => Time.ToString("h:mm tt");
        public System.Windows.Media.Brush KindColor => Kind switch
        {
            "online" => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#22c55e")),
            "game" => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3b82f6")),
            "studio" => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f59e0b")),
            _ => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6b7280")),
        };
    }

    public class NameRecord
    {
        public long Id { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool Verified { get; set; }
        public long ResolvedAtUnix { get; set; }
    }
}
