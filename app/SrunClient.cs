using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HAUTNetworkGuard
{
    /// <summary>
    /// SRUN3K 校园网认证客户端。
    /// 协议行为逐条对齐原项目 Windows/src/api.cpp 与 protocol_utils.cpp，
    /// 以保证登录结果与官方客户端一致。
    /// </summary>
    public sealed class SrunClient : IDisposable
    {
        private const string StatusUrl = "http://172.16.154.130/cgi-bin/rad_user_info";
        private const string LoginUrl = "http://172.16.154.130:69/cgi-bin/srun_portal";
        private const string PasswordKey = "1234567890";
        private const long MaxSafeCounter = 9007199254740991L;

        private readonly HttpClient _http;
        private readonly string _userAgent;

        public SrunClient(string version)
        {
            _userAgent = "HAUTNetworkGuard/" + version + " Qt";
            var handler = new HttpClientHandler
            {
                UseProxy = false,                 // 门户网关在私网，绕过系统代理更可靠
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        }

        // ---------------- 加密 ----------------

        public static string EncryptUsername(string username)
        {
            var sb = new StringBuilder(username.Length + 10);
            foreach (char c in username) sb.Append((char)(c + 4));
            return "{SRUN3}\r\n" + sb;
        }

        public static string EncryptPassword(string password)
        {
            byte[] key = Encoding.ASCII.GetBytes(PasswordKey);
            byte[] pwd = Encoding.UTF8.GetBytes(password);
            var sb = new StringBuilder(pwd.Length * 2);

            for (int i = 0; i < pwd.Length; i++)
            {
                byte c = pwd[i];
                int keyIndex = key.Length - 1 - (i % key.Length);
                byte ki = (byte)(c ^ key[keyIndex]);

                char low = (char)((ki & 0x0F) + 0x36);
                char high = (char)(((ki >> 4) & 0x0F) + 0x63);

                if (i % 2 == 0) { sb.Append(low); sb.Append(high); }
                else { sb.Append(high); sb.Append(low); }
            }
            return sb.ToString();
        }

        /// <summary>与原项目一致的 percentEncode：仅保留 unreserved 字符。</summary>
        private static string PercentEncode(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            var sb = new StringBuilder(bytes.Length * 3);
            foreach (byte b in bytes)
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        // ---------------- 登录 / 注销 ----------------

        public async Task<LoginResult> LoginAsync(string username, string password)
        {
            string encUser = EncryptUsername(username);
            string encPass = EncryptPassword(password);

            string body =
                "action=login" +
                "&username=" + PercentEncode(encUser) +
                "&password=" + PercentEncode(encPass) +
                "&ac_id=1" +
                "&drop=0" +
                "&pop=1" +
                "&type=10" +
                "&n=117" +
                "&mbytes=0" +
                "&minutes=0" +
                "&mac=02%3A00%3A00%3A00%3A00%3A00";

            return await PostAuthAsync(body, isLogin: true).ConfigureAwait(false);
        }

        public async Task<LoginResult> LogoutAsync()
        {
            return await PostAuthAsync("action=logout", isLogin: false).ConfigureAwait(false);
        }

        private async Task<LoginResult> PostAuthAsync(string body, bool isLogin)
        {
            try
            {
                using (var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"))
                {
                    content.Headers.ContentType.CharSet = null; // 与 Qt 一致：不带 charset
                    using (var req = new HttpRequestMessage(HttpMethod.Post, LoginUrl) { Content = content })
                    {
                        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                        using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                        {
                            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                                return LoginResult.Fail("校园网网关返回异常，请稍后重试。", "http_" + (int)resp.StatusCode);

                            string cls = ClassifyLoginResponse(text);
                            if (cls == "success") return LoginResult.Ok("登录成功", cls);
                            if (cls == "already_online") return LoginResult.Ok("已经在线", cls);
                            if (!isLogin && cls == "logout_ok") return LoginResult.Ok("注销成功", cls);
                            if (!isLogin && cls == "not_online") return LoginResult.Ok("当前未在线", cls);
                            return LoginResult.Fail(UserFacingLoginMessage(cls), cls);
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return LoginResult.Fail("连接校园网网关超时，请确认已连接 Wi-Fi 或有线网络后重试。", "timeout");
            }
            catch (HttpRequestException ex)
            {
                return LoginResult.Fail(UserFacingNetworkError(ex.Message), "network_error");
            }
            catch (Exception ex)
            {
                return LoginResult.Fail("网络请求失败，请检查网络连接后重试。（" + ex.GetType().Name + "）", "network_error");
            }
        }

        // ---------------- 状态检测 ----------------

        public async Task<StatusResult> CheckStatusAsync()
        {
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string url = StatusUrl + "?callback=" + Uri.EscapeDataString("jQuery_" + ms) + "&_=" + ms;

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                    req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                            return StatusResult.NetworkError();
                        return ParseStatusResponse(text);
                    }
                }
            }
            catch
            {
                return StatusResult.NetworkError();
            }
        }

        // ---------------- 响应解析 ----------------

        private static readonly Regex ErrRe = new Regex(@"E([0-9]{4})(?![0-9])", RegexOptions.Compiled);
        private static readonly Regex JsonpRe = new Regex(@"jQuery_\d+\((.+)\)$", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex DigitsRe = new Regex("^[0-9]+$", RegexOptions.Compiled);

        public static string ExtractErrorCode(string response)
        {
            var m = ErrRe.Match(response);
            return m.Success ? "E" + m.Groups[1].Value : "";
        }

        public static string ClassifyLoginResponse(string response)
        {
            string body = (response ?? "").Trim();
            if (body.Contains("login_ok")) return "success";
            if (body.Contains("already_online")) return "already_online";
            if (body.Contains("logout_ok")) return "logout_ok";
            if (body.Contains("not_online")) return "not_online";

            string code = ExtractErrorCode(body);
            if (code.Length > 0) return "error_" + code;
            if (body.Length == 0) return "empty";
            return "unknown";
        }

        public static string UserFacingLoginMessage(string classification)
        {
            switch (classification)
            {
                case "success": return "登录成功";
                case "already_online": return "已经在线";
                case "logout_ok": return "注销成功";
                case "not_online": return "当前未在线";
                case "error_E2531": return "学号或密码错误，请检查后重试。";
                case "empty": return "校园网网关返回空响应，请检查网络后重试。";
                case "unknown": return "校园网网关返回了无法识别的结果，请稍后重试。";
            }
            if (classification != null && classification.StartsWith("error_E"))
                return "登录失败（错误码 " + classification.Substring("error_".Length) + "），请稍后重试。";
            return "登录失败，请稍后重试。";
        }

        public static string UserFacingNetworkError(string error)
        {
            string n = (error ?? "").Trim().ToLowerInvariant();
            if (n.Contains("timeout") || n.Contains("timed out") || n.Contains("超时"))
                return "连接校园网网关超时，请确认已连接 Wi-Fi 或有线网络后重试。";
            if (n.Contains("host not found") || n.Contains("无法解析主机") ||
                n.Contains("cannot connect") || n.Contains("network is unreachable") ||
                n.Contains("connection refused") || n.Contains("unreachable") || n.Contains("无法连接"))
                return "无法连接校园网网关，请检查网络连接后重试。";
            return "网络请求失败，请检查网络连接后重试。";
        }

        private static bool IsValidIpv4(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string[] parts = value.Split('.');
            if (parts.Length != 4) return false;
            foreach (string p in parts)
            {
                int n;
                if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false;
                if (n < 0 || n > 255) return false;
            }
            return true;
        }

        private static long ParseCounter(object value)
        {
            if (value == null) return 0;

            if (value is string s)
            {
                long n;
                if (long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n) &&
                    n >= 0 && n <= MaxSafeCounter && DigitsRe.IsMatch(s))
                    return n;
                return 0;
            }

            try
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
                if (d < 0 || d > MaxSafeCounter) return 0;
                if (Math.Floor(d) != d) return 0;
                return (long)d;
            }
            catch { return 0; }
        }

        public static StatusResult ParseStatusResponse(string response)
        {
            var result = new StatusResult();
            string trimmed = (response ?? "").Trim();

            if (trimmed == "not_online") { result.Format = "offline"; return result; }
            if (trimmed.Length == 0) { result.Format = "unparsed"; return result; }

            string jsonStr;
            var m = JsonpRe.Match(trimmed);
            if (m.Success)
            {
                jsonStr = m.Groups[1].Value;
                result.Format = "jsonp";
            }
            else
            {
                jsonStr = trimmed;
                result.Format = "json";
            }

            // 先尝试 JSON
            Dictionary<string, object> obj = null;
            if (jsonStr.TrimStart().StartsWith("{"))
            {
                try { obj = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(jsonStr); }
                catch { obj = null; }
            }

            if (obj != null)
            {
                object errObj;
                string err = obj.TryGetValue("error", out errObj) && errObj != null ? errObj.ToString() : "";
                if (err == "not_online_error" || err.Contains("not_online"))
                {
                    result.Format = "offline";
                    return result;
                }

                object ipObj, bytesObj, secObj, userObj;
                result.Ip = obj.TryGetValue("online_ip", out ipObj) && ipObj != null ? ipObj.ToString() : "";
                result.Bytes = obj.TryGetValue("sum_bytes", out bytesObj) ? ParseCounter(bytesObj) : 0;
                result.Seconds = obj.TryGetValue("sum_seconds", out secObj) ? ParseCounter(secObj) : 0;
                result.Username = obj.TryGetValue("user_name", out userObj) && userObj != null ? userObj.ToString() : "";

                if (result.Username.Length > 0 || IsValidIpv4(result.Ip))
                {
                    result.Online = true;
                    return result;
                }
            }

            // CSV 兜底：user,seconds,ip,bytes,...
            string[] parts = trimmed.Split(',');
            if (parts.Length >= 4 && parts[0].Length > 0)
            {
                long seconds, bytes;
                bool secOk = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out seconds);
                bool bytesOk = long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out bytes);
                if (secOk && bytesOk && seconds >= 0 && bytes >= 0 &&
                    seconds <= MaxSafeCounter && bytes <= MaxSafeCounter && IsValidIpv4(parts[2]))
                {
                    result.Format = "csv";
                    result.Username = parts[0];
                    result.Seconds = seconds;
                    result.Ip = parts[2];
                    result.Bytes = bytes;
                    result.Online = true;
                    return result;
                }
            }

            result.Format = "unparsed";
            return result;
        }

        public void Dispose() { _http.Dispose(); }
    }

    public sealed class LoginResult
    {
        public bool Success;
        public string Message;
        public string Classification;

        public static LoginResult Ok(string msg, string cls)
        {
            return new LoginResult { Success = true, Message = msg, Classification = cls };
        }

        public static LoginResult Fail(string msg, string cls)
        {
            return new LoginResult { Success = false, Message = msg, Classification = cls };
        }
    }

    public sealed class StatusResult
    {
        public bool Online;
        public string Format = "offline";
        public string Username = "";
        public string Ip = "";
        public long Bytes;
        public long Seconds;

        public static StatusResult NetworkError() { return new StatusResult { Format = "network_error" }; }
    }
}
