using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Watchblox
{
    /// <summary>
    /// In-app Roblox login: a WebView2 browser pointed at roblox.com/login.
    /// The user signs in normally (password + 2FA if they have it); when the
    /// .ROBLOSECURITY session cookie appears, it is handed back to the main
    /// window, which stores it the same DPAPI-encrypted way as a pasted
    /// cookie. The cookie value is never displayed anywhere, and the
    /// password never touches the app — it only ever goes to Roblox.
    /// </summary>
    public partial class LoginWindow : Window
    {
        /// <summary>The .ROBLOSECURITY value, set when the login completes.</summary>
        public string SessionCookie { get; private set; }

        private bool _done;

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += async (_, __) => await InitAsync();
        }

        private async Task InitAsync()
        {
            try
            {
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Watchblox", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await Browser.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                // No WebView2 runtime on this PC (it ships with Edge, so
                // this is rare): say so and let the user close this and
                // paste the cookie instead.
                Browser.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = ex is WebView2RuntimeNotFoundException
                    ? "The in-app login needs the WebView2 runtime, which comes with Microsoft Edge — it isn't installed on this PC. Install Edge (or the WebView2 runtime), then try again, or close this and paste the cookie instead."
                    : "Couldn't open the Roblox login page (" + ex.Message + "). Close this and paste the cookie instead.";
                return;
            }

            Browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                // Keep the login inside Roblox: anything else opens in the
                // user's real browser instead, so a stray link can't show
                // a lookalike login page inside the app.
                string host = new Uri(e.Uri).Host;
                if (!host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase)
                    && !host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    try { System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                }
            };
            Browser.CoreWebView2.NavigationCompleted += async (_, __) => await CheckForSessionAsync();
            Browser.Source = new Uri("https://www.roblox.com/login");
            await CheckForSessionAsync();
        }

        private async Task CheckForSessionAsync()
        {
            if (_done || Browser.CoreWebView2 == null) return;
            try
            {
                var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
                var session = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY");
                if (session != null && !string.IsNullOrEmpty(session.Value))
                {
                    _done = true;
                    SessionCookie = session.Value;
                    DialogResult = true;
                }
            }
            catch { /* a failed read just waits for the next navigation */ }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
