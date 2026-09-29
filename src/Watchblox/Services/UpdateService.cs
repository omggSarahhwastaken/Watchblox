using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Watchblox.Services
{
    public class UpdateInfo
    {
        public bool Available { get; set; }
        public string Version { get; set; } = "";
        public string LocalVersion { get; set; } = "";
        public string Url { get; set; } = "";
        public string Encoding { get; set; } = "";
        public string Notes { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class UpdateService
    {
        // Secret gist: watchblox-version.json (version manifest + installer download).
        private const string ManifestUrl =
            "https://gist.githubusercontent.com/omggSarahhwastaken/56007d165b1582c55206dd647909313c/raw/watchblox-version.json";

        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            string current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Watchblox");
                    client.Timeout = TimeSpan.FromSeconds(20);
                    string url = ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string json = await client.GetStringAsync(url);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        string latest = root.GetProperty("version").GetString() ?? "";
                        string dl = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        string enc = root.TryGetProperty("encoding", out var e) ? e.GetString() ?? "" : "";
                        string notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(dl)
                            || latest == "0.0.0")
                            return new UpdateInfo
                            {
                                Available = false,
                                Message = "You're on v" + current + ". No releases published yet."
                            };
                        if (IsNewer(latest, current))
                            return new UpdateInfo
                            {
                                Available = true,
                                Version = latest,
                                LocalVersion = current,
                                Url = dl,
                                Encoding = enc,
                                Notes = notes
                            };
                        return new UpdateInfo
                        {
                            Available = false,
                            Message = "You're up to date (v" + current + ")."
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return new UpdateInfo { Available = false, Message = "Update check failed: " + ex.Message };
            }
        }

        private static bool IsNewer(string latest, string current)
        {
            try { return new Version(Normalize(latest)) > new Version(Normalize(current)); }
            catch { return false; }
        }

        /// <summary>
        /// Downloads the installer from the update manifest and launches it.
        /// The installer filename contains "_update" so it auto-closes and
        /// relaunches the app when done. Returns false if the download failed;
        /// on success this method does not return to a running app — the
        /// caller should shut down right after.
        /// </summary>
        public async Task<bool> DownloadAndInstallAsync(UpdateInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Url)) return false;
            try
            {
                string tmp = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "WatchbloxSetup_update.exe");
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Watchblox");
                    if (!string.IsNullOrEmpty(info.Encoding) &&
                        info.Encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
                    {
                        string b64 = await client.GetStringAsync(info.Url);
                        await System.IO.File.WriteAllBytesAsync(
                            tmp, Convert.FromBase64String(b64.Trim()));
                    }
                    else
                    {
                        using (var resp = await client.GetAsync(
                            info.Url, HttpCompletionOption.ResponseHeadersRead))
                        {
                            resp.EnsureSuccessStatusCode();
                            using (var fs = System.IO.File.Create(tmp))
                                await resp.Content.CopyToAsync(fs);
                        }
                    }
                }
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(tmp) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        private static string Normalize(string v)
        {
            var parts = new List<string>(v.Trim().TrimStart('v', 'V').Split('.'));
            while (parts.Count < 3) parts.Add("0");
            return string.Join(".", parts.Take(3));
        }
    }
}
