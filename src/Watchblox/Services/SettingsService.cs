using System;
using System.IO;
using System.Text.Json;
using Watchblox.Models;

namespace Watchblox.Services
{
    /// <summary>
    /// Atomic settings persistence: write to temp, then File.Replace with a
    /// .bak fallback. Main and backup parse independently so a corrupt main
    /// never blocks the backup. A failed save leaves existing files untouched.
    /// </summary>
    public class SettingsService
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watchblox");

        private readonly string _main;
        private readonly string _backup;
        public AppSettings Settings { get; private set; } = new AppSettings();
        public bool ConfigBroken { get; private set; }
        // True when Load fell back to the backup because main was corrupt.
        // The first Save after that must not clobber the good backup with
        // the corrupt main file.
        private bool _recoveredFromBackup;

        public SettingsService()
        {
            Directory.CreateDirectory(DataDir);
            _main = Path.Combine(DataDir, "settings.json");
            _backup = Path.Combine(DataDir, "settings.json.bak");
        }

        public void Load()
        {
            AppSettings main = TryParse(_main);
            AppSettings bak = TryParse(_backup);
            if (main != null) { Settings = main; ConfigBroken = false; _recoveredFromBackup = false; }
            else if (bak != null) { Settings = bak; ConfigBroken = false; _recoveredFromBackup = true; }
            else
            {
                bool anyFile = File.Exists(_main) || File.Exists(_backup);
                Settings = new AppSettings();
                ConfigBroken = anyFile; // files exist but neither parses
                _recoveredFromBackup = false;
            }
        }

        private static AppSettings TryParse(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<AppSettings>(json);
            }
            catch { return null; }
        }

        /// <summary>Returns false (leaving files untouched) if the save fails.</summary>
        public bool Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                string tmp = _main + ".tmp";
                File.WriteAllText(tmp, json);
                if (_recoveredFromBackup)
                {
                    // The on-disk main is corrupt and the good data came from
                    // the backup: drop the corrupt main instead of rotating
                    // it into the backup slot.
                    try { File.Delete(_main); } catch { }
                    File.Move(tmp, _main);
                    _recoveredFromBackup = false;
                }
                else if (File.Exists(_main))
                    File.Replace(tmp, _main, _backup);
                else
                    File.Move(tmp, _main);
                ConfigBroken = false;
                return true;
            }
            catch { return false; }
        }
    }
}
