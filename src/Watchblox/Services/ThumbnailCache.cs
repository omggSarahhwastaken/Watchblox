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
            return File.Exists(p) ? p : "";
        }

        public async Task<string> EnsureAsync(long userId, string url)
        {
            string existing = GetPath(userId);
            if (!string.IsNullOrEmpty(existing)) return existing;
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                byte[] bytes = await _api.DownloadBytesAsync(url);
                string p = Path.Combine(_dir, userId + ".png");
                await File.WriteAllBytesAsync(p, bytes);
                return p;
            }
            catch { return ""; }
        }
    }
}
