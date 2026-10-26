using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HAUTNetworkGuard
{
    internal static class Program
    {
        /// <summary>离屏自检：不显示窗口、不建托盘，加载完界面后写出诊断文件并退出。</summary>
        public static bool SelfTest;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        /// <summary>
        /// 尽早声明 DPI 感知。清单有时不生效（例如被外部启动器加载），
        /// 这里再兜一次，保证 DeviceDpi 拿到真实值、界面不被系统位图拉伸。
        /// </summary>
        private static void EnsureDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { SetProcessDpiAwareness(2); } catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            EnsureDpiAware();

            SelfTest = args != null && Array.Exists(args,
                a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

            bool created;
            using (var mutex = new Mutex(true, "HAUTNetworkGuard-LiquidGlass-SingleInstance", out created))
            {
                if (!created && !SelfTest)
                {
                    MessageBox.Show("HAUT Network Guard 已在运行。\n请查看系统托盘。",
                        "HAUT Network Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
