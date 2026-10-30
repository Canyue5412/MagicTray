using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Magic5Tray
{
    internal sealed class Engine : IDisposable
    {
        public readonly string Root, LogDir;
        public event Action<string> Message;
        public event Action<Dictionary<string, object>> Stats;
        private readonly object processLock = new object();
        private readonly object logLock = new object();
        private Process active, mount;
        private string mountIdentity = "", mountDrive = "";
        private volatile bool cancelled;
        public string LastBackup = "尚未运行备份";
        public int SkippedCount { get; private set; }
        public string SkippedReportPath { get { return Path.Combine(LogDir, "backup-skipped.txt"); } }
        private readonly List<string> skipped = new List<string>();
        private readonly HashSet<string> skippedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string skippedSource = "";
        private DateTime skippedStarted;
        // Test seams affect only local scanning, never FTP error classification.
        internal Func<string, string[]> ListLocalEntries = Directory.GetFileSystemEntries;
        internal Func<string, FileAttributes> LocalAttributes = File.GetAttributes;
        internal Func<string, Stream> OpenLocalRead = delegate(string path) { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); };
        private string StateFile { get { return Path.Combine(LogDir, "mount-owner.json"); } }
        public Engine(string root)
        {
            Root = root; LogDir = Path.Combine(root, "logs"); Directory.CreateDirectory(LogDir);
            string result = Path.Combine(LogDir, "backup-result.txt");
            if (File.Exists(result)) LastBackup = File.ReadAllText(result);
            if (File.Exists(SkippedReportPath))
            {
                using (StreamReader report = new StreamReader(SkippedReportPath))
                {
                    string first = report.ReadLine(); int count;
                    if (first != null && first.StartsWith("跳过项目：") && int.TryParse(first.Substring(5), out count)) SkippedCount = count;
                }
            }
            RestoreMount();
        }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 67108864 }; }
        public static string Quote(string s)
        {
            StringBuilder b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') b.Append('\\', slashes * 2 + 1).Append(c);
                else b.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }
        public string Safe(string text, Config c)
        {
            if (c != null && !string.IsNullOrEmpty(c.Password)) text = text.Replace(c.Password, "********");
            return text;
        }
        private void Say(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            lock (logLock)
            {
                try
                {
                    string log = Path.Combine(LogDir, "activity.log");
                    if (File.Exists(log) && new FileInfo(log).Length > 4 * 1024 * 1024) File.Move(log, log + "." + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
                    File.AppendAllText(log, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            if (Message != null) Message(line);
        }
        public string FindRclone(Config c)
        {
            if (!string.IsNullOrWhiteSpace(c.RclonePath))
            {
                if (File.Exists(c.RclonePath)) return Path.GetFullPath(c.RclonePath);
                throw new Exception("配置的 rclone.exe 不存在，请重新选择。");
            }
            List<string> paths = new List<string>(new string[] {
                Path.Combine(Root, "tools", "rclone.exe"), Path.Combine(Root, "rclone.exe"),
                @"D:\Tools\rclone\rclone-v1.75.2-windows-amd64\rclone.exe",
                @"C:\Program Files\rclone\rclone.exe", @"C:\Program Files (x86)\rclone\rclone.exe" });
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (dir.Trim().Length > 0) try { paths.Add(Path.Combine(dir.Trim().Trim('"'), "rclone.exe")); } catch { }
            foreach (string path in paths) if (File.Exists(path)) return path;
            throw new Exception("未找到 rclone.exe。请解压 packages 中的 rclone 安装包，然后在界面选择 rclone.exe。");
        }
        private ProcessStartInfo StartInfo(Config c, IList<string> args, bool credentials)
        {
            StringBuilder line = new StringBuilder();
            foreach (string arg in args) line.Append(Quote(arg)).Append(' ');
            ProcessStartInfo psi = new ProcessStartInfo(FindRclone(c), line.ToString());
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.WorkingDirectory = Root;
            // Isolate this app from the user's other rclone remotes, options and log settings.
            List<string> inherited = new List<string>();
            foreach (string key in psi.EnvironmentVariables.Keys) if (key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)) inherited.Add(key);
            foreach (string key in inherited) psi.EnvironmentVariables.Remove(key);
            psi.EnvironmentVariables["RCLONE_CONFIG"] = "NUL";
            if (credentials)
            {
                string obscure = Run(c, new string[] {"obscure", "-"}, 10000, false, c.Password + "\n", false).Trim();
                psi.EnvironmentVariables["RCLONE_CONFIG_MAGIC5_DESKTOP_TYPE"] = "ftp";
                psi.EnvironmentVariables["RCLONE_CONFIG_MAGIC5_DESKTOP_HOST"] = c.Host;
                psi.EnvironmentVariables["RCLONE_CONFIG_MAGIC5_DESKTOP_PORT"] = c.Port.ToString();
                psi.EnvironmentVariables["RCLONE_CONFIG_MAGIC5_DESKTOP_USER"] = c.User;
                psi.EnvironmentVariables["RCLONE_CONFIG_MAGIC5_DESKTOP_PASS"] = obscure;
            }
            return psi;
        }
        public void ResetCancellation() { cancelled = false; }
        public void Cancel()
        {
            cancelled = true;
            lock (processLock) { if (active != null) try { if (!active.HasExited) active.Kill(); } catch { } }
        }
        private void CheckCancelled() { if (cancelled) throw new OperationCanceledException("备份已停止，下次会重新检查未完成文件。"); }
        private sealed class RcloneFailure : Exception
        {
            public readonly bool PartialRenameDenied;
            public readonly List<string> FailedObjects;
            public readonly int ReportedErrors;
            public readonly bool HasOtherErrors;
            public RcloneFailure(int code, string details, bool partialRenameDenied, List<string> failedObjects, int reportedErrors, bool hasOtherErrors)
                : base("rclone 失败（" + code + "）：" + details) { PartialRenameDenied = partialRenameDenied; FailedObjects = failedObjects; ReportedErrors = reportedErrors; HasOtherErrors = hasOtherErrors; }
        }
        private static string ErrorDetail(string line, out string failedObject, out int errorCount, out bool otherError)
        {
            failedObject = null; errorCount = 0; otherError = false;
            try
            {
                Dictionary<string, object> row = Json().Deserialize<Dictionary<string, object>>(line);
                if (row.ContainsKey("stats"))
                {
                    Dictionary<string, object> stats = row["stats"] as Dictionary<string, object>;
                    if (stats != null && stats.ContainsKey("errors")) errorCount = Convert.ToInt32(stats["errors"]);
                    return null;
                }
                string level = row.ContainsKey("level") ? Convert.ToString(row["level"]) : "";
                if (level == "info" || level == "debug") return null;
                string detail = row.ContainsKey("msg") ? Convert.ToString(row["msg"]) : line;
                Match aggregate = Regex.Match(detail, @"^Attempt \d+/\d+ failed with (\d+) errors? and:");
                if (aggregate.Success) errorCount = int.Parse(aggregate.Groups[1].Value);
                if (level == "error" && row.ContainsKey("object") &&
                    (detail.IndexOf("failed to open source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     detail.IndexOf("failed to read source", StringComparison.OrdinalIgnoreCase) >= 0))
                    failedObject = Convert.ToString(row["object"]);
                if (level == "error" || level == "critical")
                {
                    if (row.ContainsKey("object")) otherError = failedObject == null;
                    else if (!aggregate.Success && !detail.StartsWith("Failed to copy", StringComparison.OrdinalIgnoreCase)) otherError = true;
                }
                if (row.ContainsKey("object")) detail = Convert.ToString(row["object"]) + " — " + detail;
                return detail;
            }
            catch { otherError = true; return line; }
        }
        private string Run(Config c, IList<string> args, int timeout, bool credentials, string input, bool progress)
        {
            CheckCancelled();
            ProcessStartInfo psi = StartInfo(c, args, credentials);
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            psi.RedirectStandardInput = input != null;
            StringBuilder output = new StringBuilder(), errors = new StringBuilder(); bool tooLarge = false, partialRenameDenied = false;
            List<string> failedObjects = new List<string>();
            int reportedErrors = 0; bool hasOtherErrors = false;
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (output) { if (output.Length < 64 * 1024 * 1024) output.AppendLine(e.Data); else tooLarge = true; }
                };
                p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    string safe = Safe(e.Data, c);
                    string failedObject; int errorCount; bool otherError;
                    string detail = ErrorDetail(safe, out failedObject, out errorCount, out otherError);
                    lock (errors)
                    {
                        if (!string.IsNullOrEmpty(failedObject) && !failedObjects.Contains(failedObject)) failedObjects.Add(failedObject);
                        reportedErrors = Math.Max(reportedErrors, errorCount); hasOtherErrors |= otherError;
                        if (safe.IndexOf("partial file rename failed", StringComparison.OrdinalIgnoreCase) >= 0 && safe.Contains("553")) partialRenameDenied = true;
                        if (!string.IsNullOrEmpty(detail))
                        {
                            errors.AppendLine(detail);
                            if (errors.Length > 4000) errors.Remove(0, errors.Length - 4000);
                        }
                    }
                    if (progress) OnProgress(safe);
                };
                lock (processLock) { CheckCancelled(); p.Start(); active = p; }
                try
                {
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (input != null) { byte[] bytes = Encoding.UTF8.GetBytes(input); p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.Close(); }
                    Stopwatch clock = Stopwatch.StartNew();
                    while (!p.WaitForExit(200))
                    {
                        CheckCancelled();
                        if (timeout > 0 && clock.ElapsedMilliseconds > timeout) throw new TimeoutException("操作超时，请检查手机 FTP 服务、端口和网络。");
                    }
                    p.WaitForExit(); // drain both asynchronous streams before examining the result
                    CheckCancelled();
                    if (tooLarge) throw new Exception("文件列表过大，请缩小备份文件夹范围。");
                    if (p.ExitCode != 0) throw new RcloneFailure(p.ExitCode, errors.ToString().Trim(), partialRenameDenied, failedObjects, reportedErrors, hasOtherErrors);
                    return output.ToString();
                }
                finally
                {
                    lock (processLock) { if (active == p) active = null; }
                    try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
                }
            }
        }
        private void OnProgress(string line)
        {
            try
            {
                Dictionary<string, object> row = Json().Deserialize<Dictionary<string, object>>(line);
                if (row.ContainsKey("stats"))
                {
                    if (Stats != null) Stats((Dictionary<string, object>)row["stats"]);
                    return;
                }
                string msg = row.ContainsKey("msg") ? Convert.ToString(row["msg"]) : line;
                if (row.ContainsKey("object")) msg = Convert.ToString(row["object"]) + " — " + msg;
                Say(msg);
            }
            catch { Say(line); }
        }
        private static List<string> NetworkArgs(params string[] args)
        {
            List<string> list = new List<string>(args);
            list.AddRange(new string[] {"--contimeout", "8s", "--timeout", "30s", "--ftp-idle-timeout", "30s", "--retries", "1", "--low-level-retries", "2"});
            return list;
        }
        public void Test(Config c)
        {
            c.Validate(false);
            Run(c, NetworkArgs("lsf", c.Target, "--max-depth", "1"), 45000, true, null, false);
            Say("FTP 登录成功，手机目录可读取。");
        }
        public static bool DriveOccupied(string drive)
        {
            foreach (string d in Environment.GetLogicalDrives()) if (d.StartsWith(drive, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int length, out int needed);
        public static bool IsLowIntegrity()
        {
            // Executables can inherit a Low mandatory label from their build directory.
            // Windows then starts them (and rclone) with Low integrity, even from Explorer.
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                int needed;
                GetTokenInformation(identity.Token, 25, IntPtr.Zero, 0, out needed); // TokenIntegrityLevel
                if (needed == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                IntPtr buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!GetTokenInformation(identity.Token, 25, buffer, needed, out needed))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    int count = Marshal.ReadByte(sid, 1);
                    int level = Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
                    return level < 0x2000; // SECURITY_MANDATORY_MEDIUM_RID
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        public bool HasMount { get { try { return mount != null && !mount.HasExited; } catch { return false; } } }
        public string MountedDrive { get { return mountDrive; } }
        public bool MountMatches(Config c) { return HasMount && mountIdentity == Identity(c); }
        private static string Hash(string text)
        {
            using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }
        private static string Identity(Config c) { return Hash(c.Host + "\n" + c.Port + "\n" + c.User + "\n" + c.RemotePath.Trim('/') + "\n" + c.Drive); }
        private void RestoreMount()
        {
            if (!File.Exists(StateFile)) return;
            try
            {
                Dictionary<string, string> s = Json().Deserialize<Dictionary<string, string>>(File.ReadAllText(StateFile));
                Process p = Process.GetProcessById(int.Parse(s["pid"]));
                if (p.StartTime.ToUniversalTime().Ticks.ToString() == s["start"] &&
                    string.Equals(p.MainModule.FileName, s["exe"], StringComparison.OrdinalIgnoreCase))
                { mount = p; mountIdentity = s["identity"]; mountDrive = s["drive"]; }
                else p.Dispose();
            }
            catch { }
        }
        public void Mount(Config c)
        {
            c.Validate(false);
            if (IsLowIntegrity())
                throw new Exception("当前程序以低完整性级别运行，Windows 禁止它创建盘符。请关闭工具，在普通 Windows PowerShell 中重新运行 src\\Magic5Tray\\build.ps1，修复成品文件的完整性标签；无需以管理员身份运行。");
            if (HasMount)
            {
                if (!MountMatches(c)) throw new Exception("已有其他配置的挂载，请先断开再更改连接设置。");
                if (!DriveOccupied(c.Drive)) throw new Exception("挂载进程存在但盘符未就绪，请断开后重新连接。");
                Test(c); return;
            }
            if (DriveOccupied(c.Drive)) throw new Exception(c.Drive + " 已被占用（可能是旧版挂载），请选择其他盘符或先卸载旧盘。");
            if (!File.Exists(@"C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll") && !File.Exists(@"C:\Program Files\WinFsp\bin\winfsp-x64.dll"))
                throw new Exception("尚未安装 WinFsp，请先运行 packages 中的 WinFsp 安装包。");
            Test(c);
            string cache = Path.Combine(LogDir, "cache"); Directory.CreateDirectory(cache);
            string volume = "Magic5-" + Identity(c).Substring(0, 10) + "-" + c.Drive[0];
            List<string> args = NetworkArgs("mount", c.Target, c.Drive, "--network-mode", "--volname", volume,
                "--vfs-cache-mode", "writes", "--cache-dir", cache, "--dir-cache-time", "10s", "--poll-interval", "0",
                "--log-file", Path.Combine(LogDir, "mount.log"), "--log-level", "INFO");
            if (mount != null) mount.Dispose();
            mount = Process.Start(StartInfo(c, args, true));
            mountIdentity = Identity(c); mountDrive = c.Drive;
            try
            {
                Dictionary<string, string> state = new Dictionary<string, string>();
                state["pid"] = mount.Id.ToString(); state["start"] = mount.StartTime.ToUniversalTime().Ticks.ToString();
                state["exe"] = mount.MainModule.FileName; state["identity"] = mountIdentity; state["drive"] = mountDrive;
                Config.AtomicWrite(StateFile, Json().Serialize(state));
                for (int i = 0; i < 100; i++)
                {
                    CheckCancelled();
                    if (mount.HasExited) throw new Exception(MountError());
                    if (DriveOccupied(c.Drive)) { Say("已挂载 " + c.Drive); return; }
                    Thread.Sleep(200);
                }
                throw new Exception("盘符未在 20 秒内出现，请检查 WinFsp 和 mount.log。");
            }
            catch { Unmount(); throw; }
        }
        private string MountError()
        {
            string path = Path.Combine(LogDir, "mount.log");
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length > 6000) fs.Seek(-6000, SeekOrigin.End);
                    using (StreamReader reader = new StreamReader(fs))
                    {
                        string text = reader.ReadToEnd();
                        if (text.IndexOf("c0000022", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "WinFsp 拒绝创建盘符（访问被拒绝），请查看日志中的本次挂载记录：" + path;
                        return "挂载进程退出。日志：" + path + "\r\n" + text;
                    }
                }
            }
            catch { return "挂载进程退出，请查看 " + path; }
        }
        public void Unmount()
        {
            if (HasMount) { mount.Kill(); if (!mount.WaitForExit(8000)) throw new Exception("挂载进程尚未退出，请稍后重试。"); }
            if (mount != null) { mount.Dispose(); mount = null; }
            if (File.Exists(StateFile)) File.Delete(StateFile);
            Say("已断开本工具创建的挂载。");
        }
        public static bool Online(Config c)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult a = client.BeginConnect(c.Host, c.Port, null, null);
                    using (WaitHandle wait = a.AsyncWaitHandle) { if (!wait.WaitOne(1500)) return false; client.EndConnect(a); return true; }
                }
            }
            catch { return false; }
        }
        private static bool LocalPermissionDenied(Exception ex)
        {
            return ex is UnauthorizedAccessException || ex is System.Security.SecurityException;
        }
        private void Skip(string path, string reason)
        {
            if (!skippedPaths.Add(path.TrimEnd('\\'))) return;
            skipped.Add(path + "\t" + reason); SkippedCount = skipped.Count;
            Say("跳过（未备份）：" + path + " — " + reason);
        }
        private void SaveSkippedReport(string state)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("跳过项目：" + SkippedCount);
            text.AppendLine("开始时间：" + skippedStarted.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("电脑源文件夹：" + skippedSource);
            text.AppendLine("本轮状态：" + state);
            text.AppendLine("下列项目未备份。不可访问的目录每个计 1 项，其内部未扫描、未计数；原有备份继续保留（包括 _versions 中的旧版本）。权限恢复后下轮重新尝试。");
            text.AppendLine();
            if (skipped.Count == 0) text.AppendLine("本轮没有跳过项目。");
            else foreach (string item in skipped)
            {
                int separator = item.IndexOf('\t');
                text.AppendLine(item.Substring(0, separator));
                text.AppendLine("  原因：" + item.Substring(separator + 1));
                text.AppendLine();
            }
            Config.AtomicWrite(SkippedReportPath, text.ToString());
        }
        public string ReadSkippedReport()
        {
            return File.Exists(SkippedReportPath) ? File.ReadAllText(SkippedReportPath) : "尚未运行备份，没有跳过项目记录。";
        }
        private Dictionary<string, string> Scan(string root)
        {
            Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
            Stack<string> pending = new Stack<string>(); pending.Push(root);
            byte[] buffer = new byte[1024 * 1024]; Stopwatch report = Stopwatch.StartNew();
            while (pending.Count > 0)
            {
                CheckCancelled(); string dir = pending.Pop();
                string[] entries;
                try
                {
                    if ((LocalAttributes(dir) & FileAttributes.ReparsePoint) != 0) { Skip(dir, "目录链接：避免重复或循环，其内部未扫描"); continue; }
                    entries = ListLocalEntries(dir);
                }
                catch (Exception ex)
                {
                    if (!LocalPermissionDenied(ex)) throw;
                    Skip(dir, "目录访问被拒绝，其内部未扫描"); continue;
                }
                foreach (string file in entries)
                {
                    CheckCancelled();
                    try
                    {
                        FileAttributes attributes = LocalAttributes(file);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            Skip(file, (attributes & FileAttributes.Directory) != 0 ? "目录链接：避免重复或循环，其内部未扫描" : "文件链接：不跟随链接"); continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(file); continue; }
                        FileInfo before = new FileInfo(file); long length = before.Length, ticks = before.LastWriteTimeUtc.Ticks;
                        string digest;
                        using (SHA256 hash = SHA256.Create())
                        using (Stream stream = OpenLocalRead(file))
                        {
                            int n; while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) { CheckCancelled(); hash.TransformBlock(buffer, 0, n, buffer, 0); }
                            hash.TransformFinalBlock(buffer, 0, 0); digest = BitConverter.ToString(hash.Hash).Replace("-", "");
                        }
                        FileInfo after = new FileInfo(file);
                        if (after.Length != length || after.LastWriteTimeUtc.Ticks != ticks) throw new Exception("扫描时文件发生变化，请稍后重试：" + file);
                        files[file.Substring(root.Length).Replace('\\', '/')] = length.ToString(CultureInfo.InvariantCulture) + ":" + digest;
                        if (report.ElapsedMilliseconds > 1500) { Say("正在扫描电脑文件，已计算 " + files.Count + " 个文件的内容摘要…"); report.Restart(); }
                    }
                    catch (Exception ex)
                    {
                        if (!LocalPermissionDenied(ex)) throw;
                        Skip(file, "本地访问被拒绝；若为目录，其内部未扫描");
                    }
                }
            }
            return files;
        }
        private int RemoveDeniedSources(string source, RcloneFailure failure, Dictionary<string, string> snapshot, List<string> changed)
        {
            int removed = 0;
            foreach (string item in failure.FailedObjects)
            {
                // Only exact objects selected from this source are eligible. Remote errors
                // alone are never permissions skips; the local read must also be denied now.
                string relative = item.Replace('\\', '/');
                if (!snapshot.ContainsKey(relative) || !changed.Contains(relative)) continue;
                string path = Path.Combine(source, relative.Replace('/', '\\'));
                try { using (Stream stream = OpenLocalRead(path)) { } }
                catch (Exception ex)
                {
                    if (!LocalPermissionDenied(ex)) continue;
                    Skip(path, "上传时本地读取权限被拒绝，重新检查确认无法读取");
                    snapshot.Remove(relative); changed.Remove(relative); removed++;
                }
            }
            return removed;
        }
        public void Backup(Config c)
        {
            string listFile = Path.Combine(LogDir, "backup-files-" + Guid.NewGuid().ToString("N") + ".txt");
            bool reportStarted = false; string reportState = "备份失败";
            try
            {
                c.Validate(true);
                string source = c.Source;
                string app = Path.GetFullPath(Root).TrimEnd('\\') + "\\";
                if (app.StartsWith(source, StringComparison.OrdinalIgnoreCase) || source.StartsWith(app, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("请选择工具目录以外的源文件夹，避免把密码配置、缓存和备份日志上传到手机。");
                skipped.Clear(); skippedPaths.Clear(); SkippedCount = 0;
                skippedSource = source; skippedStarted = DateTime.Now; reportStarted = true;
                SaveSkippedReport("正在运行");
                Say("准备备份：" + source + " → " + c.BackupTarget);
                Test(c);
                Say("扫描电脑文件内容，检测新增和修改…");
                Dictionary<string, string> snapshot = Scan(source);
                SaveSkippedReport("扫描完成，正在检查备份");
                string key = Hash(Identity(c) + "\n" + source.ToUpperInvariant() + "\n" + c.Destination);
                string manifest = Path.Combine(LogDir, "backup-manifest-" + key + ".json");
                Dictionary<string, string> previous = new Dictionary<string, string>();
                if (File.Exists(manifest)) try { previous = Json().Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest)); } catch { Say("上次扫描记录不可读，将重新上传全部源文件。"); }
                Run(c, NetworkArgs("mkdir", c.BackupTarget), 45000, true, null, false);
                Say("检查手机端已有备份…");
                string listing = Run(c, NetworkArgs("lsjson", c.BackupTarget, "--recursive", "--files-only"), 300000, true, null, false);
                Dictionary<string, long> remote = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (Dictionary<string, object> entry in Json().Deserialize<List<Dictionary<string, object>>>(listing))
                    remote[Convert.ToString(entry["Path"])] = Convert.ToInt64(entry["Size"]);
                List<string> changed = new List<string>();
                foreach (KeyValuePair<string, string> file in snapshot)
                {
                    string old; long size;
                    long expected = long.Parse(file.Value.Substring(0, file.Value.IndexOf(':')), CultureInfo.InvariantCulture);
                    if (!previous.TryGetValue(file.Key, out old) || old != file.Value || !remote.TryGetValue(file.Key, out size) || size != expected) changed.Add(file.Key);
                }
                Say("本轮检查 " + snapshot.Count + " 个文件，需要上传 " + changed.Count + " 个。");
                if (changed.Count > 0)
                {
                    string versions = c.BackupTarget + "_versions/" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    bool direct = false; int attempt = 0;
                    while (true)
                    {
                        CheckCancelled();
                        File.WriteAllLines(listFile, changed, new UTF8Encoding(false));
                        string attemptVersions = versions + (attempt == 0 ? "" : "-retry-" + attempt) + (direct ? "-compat" : "");
                        List<string> copyArgs = NetworkArgs("copy", source, c.BackupTarget, "--backup-dir", attemptVersions, "--files-from-raw", listFile, "--ignore-times",
                            "--transfers", "1", "--checkers", "1", "--stats", "1s", "--stats-log-level", "INFO", "--use-json-log", "--log-level", "INFO");
                        if (direct) copyArgs.Add("--inplace");
                        try { Run(c, copyArgs, 0, true, null, true); break; }
                        catch (RcloneFailure failure)
                        {
                            int denied = RemoveDeniedSources(source, failure, snapshot, changed);
                            bool useCompatibility = failure.PartialRenameDenied && !direct;
                            if (changed.Count == 0)
                            {
                                if (denied > 0 && denied == failure.FailedObjects.Count && denied == failure.ReportedErrors && !failure.HasOtherErrors)
                                {
                                    Say("所有待传文件均已确认本地读取权限被拒绝，已记入跳过清单。"); break;
                                }
                                throw;
                            }
                            if (!useCompatibility && denied == 0) throw;
                            if (useCompatibility)
                            {
                                direct = true;
                                Say("手机 FTP 拒绝临时文件改名，正在用直接写入兼容模式重试本轮文件；原有旧版本继续保留。");
                            }
                            if (denied > 0) Say("已跳过本地权限变更的 " + denied + " 个文件，继续备份其余可读文件。");
                            // Every retry uses its own directory to preserve original versions,
                            // including files already uploaded successfully by an earlier try.
                            attempt++;
                        }
                    }
                }
                CheckCancelled();
                // Persist the pre-transfer content snapshot: edits made during upload are picked up next time.
                Config.AtomicWrite(manifest, Json().Serialize(snapshot));
                reportState = SkippedCount == 0 ? "备份完成" : (snapshot.Count == 0 ? "未备份任何文件" : "备份完成，" + SkippedCount + " 项跳过（未备份）");
                LastBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                    (SkippedCount == 0 ? "备份成功" : (snapshot.Count == 0 ? "未备份任何文件，" + SkippedCount + " 项跳过（未备份）" : reportState)) +
                    "：检查 " + snapshot.Count + "，上传 " + changed.Count + " 个文件。";
                SaveResult(); Say(LastBackup);
            }
            catch (Exception ex)
            {
                reportState = ex is OperationCanceledException ? "已停止，扫描或上传未完成" : "备份失败，扫描或上传未完成";
                LastBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (ex is OperationCanceledException ? "  已停止" : "  备份失败") + "：" + Safe(ex.Message, c);
                if (reportStarted && SkippedCount > 0) LastBackup += "（另有 " + SkippedCount + " 项跳过，详见跳过项目）";
                SaveResult(); throw;
            }
            finally { if (File.Exists(listFile)) File.Delete(listFile); if (reportStarted) SaveSkippedReport(reportState); }
        }
        private void SaveResult() { Config.AtomicWrite(Path.Combine(LogDir, "backup-result.txt"), LastBackup); }
        public void Dispose() { Cancel(); if (mount != null) mount.Dispose(); }
    }
}
