using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HAUTNetworkGuard
{
    public sealed class MainForm : Form
    {
        public const string AppVersion = "1.4.0";

        private readonly WebView2 _web = new WebView2();
        private readonly SrunClient _api = new SrunClient(AppVersion);
        private readonly AppConfig _cfg = AppConfig.Load();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly Timer _pollTimer = new Timer();

        private NotifyIcon _tray;
        private bool _webReady;
        private bool _busy;
        private bool _manualHold;
        private bool _reallyExit;
        private string _conn = "offline";
        private bool _gotReady;
        private bool _fitted;
        private string _ip = "";
        private long _bytes;
        private long _seconds;

        // ---------------- Win32 ----------------

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;
        private const int HTBOTTOMRIGHT = 17;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        public MainForm()
        {
            Text = "HAUT Network Guard";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(392, 560);
            Size = new Size(470, 648);
            BackColor = Color.FromArgb(233, 236, 241);
            DoubleBuffered = true;
            KeyPreview = true;
            Icon = BuildIcon();

            if (Program.SelfTest)
            {
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-6000, -6000);
            }

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Color.FromArgb(233, 236, 241);
            Controls.Add(_web);

            Load += OnLoadAsync;
            FormClosing += OnFormClosing;
        }

        private async void OnLoadAsync(object sender, EventArgs e)
        {
            ApplyRoundedCorners();
            if (!Program.SelfTest) BuildTray();
            await InitWebViewAsync();
            StartPolling();
        }

        /// <summary>
        /// 让窗口的 CSS 视口等于设计宽度（470px）。
        ///
        /// 不能直接信任 DeviceDpi：WebView2 自己按显示器 DPI 设定光栅化比例，
        /// 实测会出现「控件 470px 宽、CSS 只拿到 376px」的错位。
        /// 所以先量一次 window.innerWidth，再按实际比例反推窗口尺寸，
        /// 这样在任何缩放比下都能得到一致的版面。
        /// </summary>
        private async void FitToDesignWidth()
        {
            if (_fitted) return;
            _fitted = true;
            try
            {
                string raw = await _web.CoreWebView2.ExecuteScriptAsync("window.innerWidth");
                int cssW;
                if (!int.TryParse(raw, out cssW) || cssW <= 0) return;

                float ratio = 470f / cssW;
                if (ratio < 1.02f || ratio > 3f) return;

                Size = new Size((int)Math.Round(Width * ratio), (int)Math.Round(Height * ratio));
                MinimumSize = new Size((int)Math.Round(392 * ratio), (int)Math.Round(580 * ratio));

                if (!Program.SelfTest) CenterToScreen();
            }
            catch { }
        }

        private void ApplyRoundedCorners()
        {
            try
            {
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* Win10 无此属性，忽略 */ }
        }

        // ---------------- WebView ----------------

        private async Task InitWebViewAsync()
        {
            string wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HAUTNetworkGuard", "WebView2");

            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await _web.EnsureCoreWebView2Async(env);

                var core = _web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;

                core.SetVirtualHostNameToFolderMapping(
                    "app.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

                core.WebMessageReceived += OnWebMessage;
                core.NavigationCompleted += (s, a) =>
                {
                    _webReady = true;
                    PushConfig();
                    PushState();
                    FitToDesignWidth();
                    if (Program.SelfTest) StartSelfTest();
                };

                core.Navigate("https://app.local/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "界面组件（WebView2）初始化失败。\n\n" +
                    "请确认系统已安装 Microsoft Edge WebView2 Runtime。\n" +
                    "Windows 11 与较新的 Windows 10 默认已自带。\n\n" +
                    "详细信息：" + ex.Message,
                    "HAUT Network Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; }

            Dictionary<string, object> msg;
            try { msg = _json.Deserialize<Dictionary<string, object>>(raw); }
            catch { return; }

            string t = Str(msg, "t");
            switch (t)
            {
                case "ready":
                    _gotReady = true;
                    PushConfig();
                    PushState();
                    PushLog("应用启动，界面已就绪", "info");
                    break;

                case "login":
                    _ = DoLoginAsync(Str(msg, "user"), Str(msg, "pass"), false);
                    break;

                case "logout":
                    _ = DoLogoutAsync();
                    break;

                case "check":
                    _ = CheckAsync(true);
                    break;

                case "save":
                    SaveFromUi(msg);
                    break;

                case "win":
                    HandleWindow(Str(msg, "a"));
                    break;

                case "openUrl":
                    OpenUrl(Str(msg, "url"));
                    break;

                case "pin":
                    TopMost = Bool(msg, "v", false);
                    break;

                case "hide":
                    HideToTray();
                    break;
            }
        }

        private void StartSelfTest()
        {
            var t = new Timer { Interval = 1500 };
            t.Tick += async (s, e) =>
            {
                t.Stop();
                t.Dispose();
                var lines = new List<string>();
                try
                {
                    string js =
                        "JSON.stringify({title:document.title," +
                        "native:document.documentElement.classList.contains('native')," +
                        "bridge:!!(window.chrome&&window.chrome.webview)," +
                        "cards:document.querySelectorAll('.card').length," +
                        "views:document.querySelectorAll('.view').length," +
                        "w:innerWidth,h:innerHeight})";
                    string r = await _web.CoreWebView2.ExecuteScriptAsync(js);
                    lines.Add("RESULT=OK");
                    lines.Add("JS=" + r);

                    string tmp = Path.GetTempPath();
                    await CaptureAsync(Path.Combine(tmp, "haut-shot-status.png"));

                    await _web.CoreWebView2.ExecuteScriptAsync(
                        "document.querySelector('.side-btn[data-nav=\"account\"],.nav-btn[data-nav=\"account\"]').click()");
                    await Task.Delay(700);
                    await CaptureAsync(Path.Combine(tmp, "haut-shot-account.png"));

                    await _web.CoreWebView2.ExecuteScriptAsync(
                        "document.querySelector('.side-btn[data-nav=\"settings\"],.nav-btn[data-nav=\"settings\"]').click()");
                    await Task.Delay(700);
                    await CaptureAsync(Path.Combine(tmp, "haut-shot-settings.png"));

                    lines.Add("SHOT_DIR=" + tmp);
                    lines.Add("ENC_USER=" + SrunClient.EncryptUsername("2023123456").Replace("\r", "\\r").Replace("\n", "\\n"));
                    lines.Add("ENC_PASS=" + SrunClient.EncryptPassword("test123"));
                    lines.Add("CLASSIFY_LOGIN_OK=" + SrunClient.ClassifyLoginResponse("login_ok"));
                    lines.Add("CLASSIFY_E2531=" + SrunClient.ClassifyLoginResponse("E2531: user not found"));
                    var st = SrunClient.ParseStatusResponse("jQuery_1({\"user_name\":\"2023123456\",\"online_ip\":\"172.16.20.33\",\"sum_bytes\":\"1048576\",\"sum_seconds\":\"3600\"})");
                    lines.Add("PARSE_JSONP=" + st.Online + "|" + st.Ip + "|" + st.Bytes + "|" + st.Seconds + "|" + st.Format);
                    var st2 = SrunClient.ParseStatusResponse("2023123456,3600,172.16.20.33,1048576,0");
                    lines.Add("PARSE_CSV=" + st2.Online + "|" + st2.Ip + "|" + st2.Bytes + "|" + st2.Seconds + "|" + st2.Format);
                    var st3 = SrunClient.ParseStatusResponse("not_online");
                    lines.Add("PARSE_OFFLINE=" + st3.Online + "|" + st3.Format);

                    // 端到端：从界面发起一次登录，走完 JS -> C# -> HTTP -> 回推 的完整链路
                    await _web.CoreWebView2.ExecuteScriptAsync(
                        "document.querySelector('.side-btn[data-nav=\"status\"],.nav-btn[data-nav=\"status\"]').click()");
                    await Task.Delay(400);
                    await _web.CoreWebView2.ExecuteScriptAsync(
                        "window.chrome.webview.postMessage(JSON.stringify({t:'login',user:'2023123456',pass:'test'}))");
                    await Task.Delay(11000);
                    await CaptureAsync(Path.Combine(tmp, "haut-shot-login.png"));
                    lines.Add("AFTER_LOGIN_STATE=" + _conn);
                }
                catch (Exception ex)
                {
                    lines.Add("RESULT=FAIL");
                    lines.Add("ERROR=" + ex.Message);
                }
                lines.Add("FORM=" + Width + "x" + Height + " dpi=" + DeviceDpi + " web=" + _web.Width + "x" + _web.Height);
                lines.Add("READY_MESSAGE=" + (_gotReady ? "yes" : "no"));
                lines.Add("VERSION=" + AppVersion);
                lines.Add("TIME=" + DateTime.Now.ToString("s"));

                try
                {
                    File.WriteAllLines(Path.Combine(Path.GetTempPath(), "haut-selftest.txt"), lines, Encoding.UTF8);
                }
                catch { }

                _reallyExit = true;
                BeginInvoke(new Action(Close));
            };
            t.Start();
        }

        private async Task CaptureAsync(string path)
        {
            try
            {
                using (var fs = File.Create(path))
                {
                    await _web.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, fs);
                }
            }
            catch { }
        }

        private void HandleWindow(string action)
        {
            switch (action)
            {
                case "drag":
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    break;
                case "resize":
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTBOTTOMRIGHT, IntPtr.Zero);
                    break;
                case "min":
                case "close":
                    HideToTray();
                    break;
            }
        }

        private void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        // ---------------- 业务 ----------------

        private async Task DoLoginAsync(string user, string pass, bool silent)
        {
            if (_busy) return;

            if (string.IsNullOrEmpty(user)) user = _cfg.Username;
            if (string.IsNullOrEmpty(pass)) pass = _cfg.Password;

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                if (!silent)
                {
                    Send("{\"e\":\"needCredentials\",\"user\":" + (_json.Serialize(string.IsNullOrEmpty(user))) + "}");
                    Toast("请先填写账号", string.IsNullOrEmpty(user) ? "学号不能为空" : "密码不能为空", "err");
                }
                return;
            }

            _busy = true;
            _manualHold = false;
            SendBusy("login");
            if (!silent) PushLog("正在提交登录请求…", "info");

            var result = await _api.LoginAsync(user, pass);

            _busy = false;
            SendBusy(null);

            if (result.Success)
            {
                _cfg.Username = user;
                if (_cfg.Remember && !string.IsNullOrEmpty(pass)) _cfg.Password = pass;
                _cfg.Save();

                PushLog(result.Message, "ok");
                Toast("登录成功", result.Message, "ok");
                await CheckAsync(false);
            }
            else
            {
                PushLog(result.Message, "err");
                Toast("登录失败", result.Message, "err");
                SetState("offline", "", 0, 0);
            }
        }

        private async Task DoLogoutAsync()
        {
            if (_busy) return;
            _busy = true;
            _manualHold = true;
            SendBusy("logout");
            PushLog("正在执行注销请求…", "info");

            var result = await _api.LogoutAsync();

            _busy = false;
            SendBusy(null);

            if (result.Success)
            {
                PushLog(result.Message, "err");
                Toast("注销成功", "已断开校园网连接", "info");
            }
            else
            {
                PushLog(result.Message, "err");
                Toast("注销失败", result.Message, "err");
            }
            await CheckAsync(false);
        }

        private async Task CheckAsync(bool manual)
        {
            if (_busy) return;

            if (manual)
            {
                SetState("checking", "", 0, 0);
            }

            var st = await _api.CheckStatusAsync();

            if (st.Online)
            {
                SetState("online", st.Ip, st.Bytes, st.Seconds);
                if (manual) Toast("检测完成", "当前在线", "info");
                return;
            }

            if (st.Format == "network_error" || st.Format == "unparsed")
            {
                SetState("error", "", 0, 0);
                if (manual) Toast("检测异常", "无法连接校园网网关，请检查网络", "err");
                return;
            }

            SetState("offline", "", 0, 0);

            if (_cfg.AutoLogin && !_manualHold && !string.IsNullOrEmpty(_cfg.Password) && !string.IsNullOrEmpty(_cfg.Username))
            {
                PushLog("检测到离线，准备自动重连…", "info");
                await Task.Delay(1200);
                await DoLoginAsync(_cfg.Username, _cfg.Password, true);
            }
        }

        private void SaveFromUi(Dictionary<string, object> msg)
        {
            _cfg.Username = Str(msg, "user");
            string pass = Str(msg, "pass");
            if (!string.IsNullOrEmpty(pass)) _cfg.Password = pass;
            _cfg.Remember = Bool(msg, "remember", _cfg.Remember);
            _cfg.AutoLogin = Bool(msg, "autologin", _cfg.AutoLogin);
            _cfg.Interval = Math.Max(30, Math.Min(300, Int(msg, "interval", _cfg.Interval)));
            _cfg.Theme = Str(msg, "theme");

            bool wantAutoStart = Bool(msg, "autostart", _cfg.AutoStart);
            if (wantAutoStart != _cfg.AutoStart)
            {
                AppConfig.SetAutoStart(wantAutoStart);
                _cfg.AutoStart = wantAutoStart;
            }

            _cfg.Save();
            StartPolling();
            PushConfig();
            PushLog("设置已保存并立即生效", "ok");
            Toast("设置已保存", "新的检测间隔与运行策略已生效", "ok");
        }

        private void StartPolling()
        {
            _pollTimer.Stop();
            _pollTimer.Interval = Math.Max(30, _cfg.Interval) * 1000;
            _pollTimer.Tick -= OnPollTick;
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();
        }

        private async void OnPollTick(object sender, EventArgs e)
        {
            if (!_webReady) return;
            await CheckAsync(false);
        }

        // ---------------- 托盘 ----------------

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => RestoreFromTray());
            menu.Items.Add("立即检测", null, async (s, e) => { RestoreFromTray(); await CheckAsync(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出程序", null, (s, e) => { _reallyExit = true; Close(); });

            _tray = new NotifyIcon
            {
                Icon = Icon,
                Text = "HAUT Network Guard",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            if (_tray != null) _tray.ShowBalloonTip(1500, "HAUT Network Guard", "程序仍在后台保持连接", ToolTipIcon.Info);
            PushLog("窗口已隐藏，程序驻留系统托盘", "info");
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _pollTimer.Stop();
            _api.Dispose();
        }

        // ---------------- 与界面通信 ----------------

        private void Send(string json)
        {
            if (!_webReady || _web.CoreWebView2 == null) return;
            try { _web.CoreWebView2.PostWebMessageAsString(json); }
            catch { }
        }

        private void SetState(string conn, string ip, long bytes, long seconds)
        {
            _conn = conn; _ip = ip; _bytes = bytes; _seconds = seconds;
            PushState();

            if (_tray != null)
            {
                _tray.Text = conn == "online"
                    ? "HAUT Network Guard · 已连接 " + ip
                    : "HAUT Network Guard · 未连接";
            }
        }

        private void PushState()
        {
            var d = new Dictionary<string, object>
            {
                { "e", "state" },
                { "conn", _conn },
                { "ip", _ip },
                { "bytes", _bytes },
                { "seconds", _seconds }
            };
            Send(_json.Serialize(d));
        }

        private void PushConfig()
        {
            var d = new Dictionary<string, object>
            {
                { "e", "config" },
                { "user", _cfg.Username },
                { "hasPass", !string.IsNullOrEmpty(_cfg.Password) },
                { "remember", _cfg.Remember },
                { "autostart", _cfg.AutoStart },
                { "autologin", _cfg.AutoLogin },
                { "interval", _cfg.Interval },
                { "theme", _cfg.Theme ?? "light" },
                { "version", AppVersion }
            };
            Send(_json.Serialize(d));
        }

        private void SendBusy(string kind)
        {
            var d = new Dictionary<string, object> { { "e", "busy" }, { "v", kind ?? "" } };
            Send(_json.Serialize(d));
        }

        private void PushLog(string text, string type)
        {
            var d = new Dictionary<string, object>
            {
                { "e", "log" },
                { "text", text },
                { "type", type ?? "info" },
                { "time", DateTime.Now.ToString("HH:mm:ss") }
            };
            Send(_json.Serialize(d));
        }

        private void Toast(string title, string msg, string type)
        {
            var d = new Dictionary<string, object>
            {
                { "e", "toast" },
                { "title", title },
                { "msg", msg ?? "" },
                { "type", type ?? "info" }
            };
            Send(_json.Serialize(d));
        }

        // ---------------- 工具 ----------------

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return v.ToString();
            return "";
        }

        private static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                if (v is bool b) return b;
                bool parsed;
                if (bool.TryParse(v.ToString(), out parsed)) return parsed;
            }
            return fallback;
        }

        private static int Int(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                int parsed;
                if (int.TryParse(v.ToString(), out parsed)) return parsed;
            }
            return fallback;
        }

        private static Icon BuildIcon()
        {
            using (var bmp = new Bitmap(64, 64))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var path = RoundedRect(new Rectangle(2, 2, 60, 60), 18))
                using (var brush = new LinearGradientBrush(new Rectangle(2, 2, 60, 60),
                           Color.FromArgb(78, 158, 255), Color.FromArgb(10, 124, 240), 90f))
                {
                    g.FillPath(brush, path);
                }

                using (var pen = new Pen(Color.White, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(18, 33),
                        new PointF(28, 43),
                        new PointF(46, 22)
                    });
                }

                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
