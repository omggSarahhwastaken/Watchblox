using System;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Watchblox.Services
{
    public static class ToastService
    {
        public static void Show(string title, string message)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(message)
                    .Show();
            }
            catch { /* toasts are best-effort */ }
        }
    }
}
