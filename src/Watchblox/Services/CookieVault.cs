using System;
using System.Security.Cryptography;
using System.Text;

namespace Watchblox.Services
{
    /// <summary>
    /// DPAPI vault for the Roblox session cookie. The plaintext cookie only
    /// ever lives in memory; disk holds just the CurrentUser-encrypted blob,
    /// which no other Windows user (or PC) can decrypt.
    /// </summary>
    public static class CookieVault
    {
        public static string Protect(string cookie)
        {
            byte[] data = Encoding.UTF8.GetBytes(cookie);
            try
            {
                return Convert.ToBase64String(
                    ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
            }
            finally { Array.Clear(data, 0, data.Length); }
        }

        public static string Unprotect(string blob)
        {
            byte[] data = ProtectedData.Unprotect(
                Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(data); }
            finally { Array.Clear(data, 0, data.Length); }
        }
    }
}
