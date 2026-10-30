using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Magic5Tray
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string root = AppDomain.CurrentDomain.BaseDirectory;
                Config cfg = Config.Load(Path.Combine(root, "Magic5Tray.ini"));
                using (Engine engine = new Engine(root))
                {
                    bool created;
                    using (Mutex mutex = new Mutex(true, "Magic5Tray_SingleInstance", out created))
                    {
                        if (!created) { MessageBox.Show("网盘工具已经在运行，请使用已打开的窗口。", "Magic5 网盘"); return; }
                        if (args.Length > 0 && args[0] == "--check")
                        {
                            try { engine.Test(cfg); File.WriteAllText(Path.Combine(engine.LogDir, "check.log"), "FTP 登录及目录读取成功\r\n" + DateTime.Now); }
                            catch (Exception ex) { File.WriteAllText(Path.Combine(engine.LogDir, "check.log"), engine.Safe(ex.Message, cfg)); Environment.ExitCode = 1; }
                            return;
                        }
                        if (args.Length > 0 && args[0] == "--mount") { engine.Mount(cfg); return; }
                        if (args.Length > 0 && args[0] == "--unmount") { engine.Unmount(); return; }
                        Application.Run(new MainForm(cfg, engine));
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Magic5 网盘 - 操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
        }
    }
}
