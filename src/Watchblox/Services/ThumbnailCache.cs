using System;
using System.IO;
using System.Threading.Tasks;

namespace Watchblox.Services
{
    /// <summary>Avatar headshots cached on disk, keyed by user ID.</summary>
    public class ThumbnailCache
    {
        private readonly string _dir;
        private readonly RobloxApiClient _api;

        public ThumbnailCache(RobloxApiClient api)
        {
            _api = api;
            _dir = Path.Combine(SettingsService.DataDir, "thumbnails");
            Directory.CreateDirectory(_dir);
        }

        public string GetPath(long userId)
        {
            string p = Path.Combine(_dir, userId + ".png");
            // A 0-byte file is a torn write, not a valid avatar: treat it
            // as missing so it re-downloads instead of staying broken forever.
            if (!File.Exists(p)) return "";
            try { if (new FileInfo(p).Length == 0) return ""; } catch { return ""; }
            return p;
        }

        public async Task<string> EnsureAsync(long userId, string url)
        {
            string existing = GetPath(userId);
            if (!string.IsNullOrEmpty(existing)) return existing;
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                byte[] bytes = await _api.DownloadBytesAsync(url);
                if (bytes == null || bytes.Length == 0) return "";
                string p = Path.Combine(_dir, userId + ".png");
                string tmp = Path.Combine(_dir, userId + "." + Guid.NewGuid().ToString("N") + ".tmp");
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, p, overwrite: true);
                return p;
            }
            catch { return ""; }
        }
    }
}
