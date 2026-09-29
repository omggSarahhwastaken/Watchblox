using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;

namespace Watchblox
{
    public partial class App : Application
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SetCurrentProcessExplicitAppUserModelID(string id);

        protected override void OnStartup(StartupEventArgs e)
        {
            try { SetCurrentProcessExplicitAppUserModelID("Sarah.Watchblox"); } catch { }
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
