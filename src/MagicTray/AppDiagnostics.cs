using System;
using System.Text;

namespace MagicTray
{
    internal static class AppDiagnostics
    {
        internal static string Create(Engine engine, Config cfg, bool busy, bool backingUp)
        {
            // Build from an allowlist of facts, never from logs, exception messages,
            // configuration values or the human-readable backup result.
            bool rcloneFound;
            try { engine.FindRclone(cfg); rcloneFound = true; } catch { rcloneFound = false; }
            string integrity;
            try { integrity = Engine.IsLowIntegrity() ? "Low（挂载不可用）" : "普通或更高"; } catch { integrity = "无法检测"; }
            string drive;
            try { drive = engine.HasMount ? "本工具已挂载" : (Engine.DriveOccupied(cfg.Drive) ? "所选盘符已被占用" : "所选盘符空闲"); }
            catch { drive = "无法检测"; }
            StringBuilder text = new StringBuilder();
            text.AppendLine("MagicTray " + typeof(AppDiagnostics).Assembly.GetName().Version.ToString(3));
            text.AppendLine(".NET 运行时：" + Environment.Version);
            text.AppendLine("完整性：" + integrity);
            text.AppendLine("rclone：" + (rcloneFound ? "已找到" : "未找到（检查高级设置）"));
            text.AppendLine("WinFsp：" + (Engine.WinFspInstalled ? "已找到" : "未找到（仅备份不需要）"));
            text.AppendLine("操作状态：" + (backingUp ? "备份进行中" : (busy ? "连接操作进行中" : "空闲")));
            text.AppendLine("挂载状态：" + drive);
            text.AppendLine("自动连接：" + (cfg.AutoConnect ? "启用" : "关闭"));
            text.AppendLine("自动备份：" + (cfg.AutoBackup ? "启用" : "关闭"));
            text.AppendLine("上传并发：" + cfg.BackupTransfers);
            if (!busy && engine.LastBackupElapsedSeconds >= 0)
            {
                text.AppendLine("最近结束的备份（本次打开后）：已上传 " + engine.LastUploaded + "，待传 " + engine.LastPending + "，仅大小核对 " + engine.LastProvisional + "，跳过 " + engine.SkippedCount);
                text.AppendLine("本轮耗时：" + Engine.FormatDuration(engine.LastBackupElapsedSeconds));
            }
            text.AppendLine("此摘要未包含地址、账号、密码、文件路径或原始日志；未执行 FTP 连接测试。");
            return text.ToString();
        }
    }
}
