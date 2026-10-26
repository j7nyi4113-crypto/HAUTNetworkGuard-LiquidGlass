using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace HAUTNetworkGuard
{
    /// <summary>
    /// 本地配置。密码用 DPAPI（当前用户）加密后落盘，与原版 Windows 客户端一致。
    /// </summary>
    public sealed class AppConfig
    {
        public string Username { get; set; } = "";
        public string PasswordCipher { get; set; } = "";
        public bool Remember { get; set; } = true;
        public bool AutoStart { get; set; }
        public bool AutoLogin { get; set; } = true;
        public int Interval { get; set; } = 30;
        public string Theme { get; set; } = "light";

        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HAUTNetworkGuard");

        private static readonly string FilePath = Path.Combine(Dir, "config.json");

        [ScriptIgnore]
        public string Password
        {
            get { return Unprotect(PasswordCipher); }
            set { PasswordCipher = Protect(value ?? ""); }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppConfig();
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                var cfg = new JavaScriptSerializer().Deserialize<AppConfig>(json);
                return cfg ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string json = new JavaScriptSerializer().Serialize(this);
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            }
            catch { /* 配置写入失败不应影响主流程 */ }
        }

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch { return ""; }
        }

        private static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            try
            {
                byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }

        // ---------------- 开机自启 ----------------

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "HAUTNetworkGuard";

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RunName) != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enabled)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enabled)
                        k.SetValue(RunName, "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                    else if (k.GetValue(RunName) != null)
                        k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }
    }
}
