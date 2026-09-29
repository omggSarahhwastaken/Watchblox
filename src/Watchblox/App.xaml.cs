using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Data;

namespace Watchblox
{
    public partial class App : Application
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SetCurrentProcessExplicitAppUserModelID(string id);

        private static Mutex _instanceMutex;
        private static EventWaitHandle _showEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            try { SetCurrentProcessExplicitAppUserModelID("Sarah.Watchblox"); } catch { }

            // Single instance: a second launch (e.g. relaunching after the
            // window hid to the tray) wakes the running copy instead of
            // starting a second poller that could stomp its settings.
            bool created = false;
            try { _instanceMutex = new Mutex(true, @"Local\Sarah.Watchblox.SingleInstance", out created); }
            catch { created = true; }
            if (!created)
            {
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(@"Local\Sarah.Watchblox.ShowMe"))
                        ev.Set();
                }
                catch { }
                Shutdown();
                return;
            }
            try
            {
                _showEvent = new EventWaitHandle(
                    false, EventResetMode.AutoReset, @"Local\Sarah.Watchblox.ShowMe");
                var t = new Thread(() =>
                {
                    while (true)
                    {
                        _showEvent.WaitOne();
                        try { Dispatcher.Invoke(() => (MainWindow as MainWindow)?.RestoreFromTray()); }
                        catch { }
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch { }

            base.OnStartup(e);
        }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
            throw new NotImplementedException();
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
            throw new NotImplementedException();
    }
}
