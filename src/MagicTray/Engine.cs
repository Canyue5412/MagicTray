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

namespace MagicTray
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
        public int LastUploaded { get; private set; }
        public int LastPending { get; private set; }
        public int LastProvisional { get; private set; }
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
                @"C:\Program Files\rclone\rclone.exe", @"C:\Program Files (x86)\rclone\rclone.exe" });
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (dir.Trim().Length > 0) try { paths.Add(Path.Combine(dir.Trim().Trim('"'), "rclone.exe")); } catch { }
            foreach (string path in paths) if (File.Exists(path)) return path;
            throw new Exception("未找到 rclone.exe。请从 rclone.org 下载并解压，在“FTP 连接”的高级设置中选择程序，或将它加入 PATH。");
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
                string prefix = "RCLONE_CONFIG_" + Config.RemoteName.ToUpperInvariant() + "_";
                psi.EnvironmentVariables[prefix + "TYPE"] = "ftp";
                psi.EnvironmentVariables[prefix + "HOST"] = c.Host;
                psi.EnvironmentVariables[prefix + "PORT"] = c.Port.ToString();
                psi.EnvironmentVariables[prefix + "USER"] = c.User;
                psi.EnvironmentVariables[prefix + "PASS"] = obscure;
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
            public readonly List<string> LocalReadObjects;
            public readonly List<string> SucceededObjects;
            public readonly int ReportedErrors;
            public readonly bool HasOtherErrors;
            public readonly bool Transient;
            public RcloneFailure(int code, string details, bool partialRenameDenied, List<string> failedObjects, List<string> localReadObjects, List<string> succeededObjects, int reportedErrors, bool hasOtherErrors, bool transient)
                : base("rclone 失败（" + code + "）：" + details)
            {
                PartialRenameDenied = partialRenameDenied; FailedObjects = failedObjects; LocalReadObjects = localReadObjects; SucceededObjects = succeededObjects;
                ReportedErrors = reportedErrors; HasOtherErrors = hasOtherErrors; Transient = transient;
            }
        }
        private static bool IsTransientMessage(string text)
        {
            string lower = text.ToLowerInvariant();
            string[] markers = new string[] {
                "connection", "eof", "broken pipe", "timeout", "timed out", "wsasend", "wsarecv",
                "forcibly closed", "aborted", "421 ", "425 ", "426 ", "451 ", "temporary failure",
                "i/o error", "network is unreachable", "connection reset", "no connection could be made",
                "actively refused", "no such directory"
            };
            foreach (string marker in markers) if (lower.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            // A concurrent MKD race can surface as 550/553 "File exists"/"directory exists".
            if ((lower.IndexOf("550", StringComparison.Ordinal) >= 0 || lower.IndexOf("553", StringComparison.Ordinal) >= 0) &&
                (lower.IndexOf("directory", StringComparison.Ordinal) >= 0 || lower.IndexOf("mkdir", StringComparison.Ordinal) >= 0 || lower.IndexOf("exists", StringComparison.Ordinal) >= 0))
                return true;
            return false;
        }
        private static string SuccessObject(string line)
        {
            try
            {
                Dictionary<string, object> row = Json().Deserialize<Dictionary<string, object>>(line);
                if (!row.ContainsKey("level") || Convert.ToString(row["level"]) != "info") return null;
                string message = row.ContainsKey("msg") ? Convert.ToString(row["msg"]) : "";
                if (message == null || !message.StartsWith("Copied", StringComparison.OrdinalIgnoreCase)) return null;
                object failed;
                return row.TryGetValue("object", out failed) ? Convert.ToString(failed) : null;
            }
            catch { return null; }
        }
        private static bool IsPartialRenameDenied(string detail)
        {
            // EOF/timeouts after RNTO are ordinary connection failures. Direct writes
            // are justified only when the server explicitly rejects the rename.
            return detail.IndexOf("partial file rename failed", StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(detail, @"\b55[03]\b");
        }
        private static string ErrorDetail(string line, out string failedObject, out int errorCount, out bool otherError, out bool transient, out bool localReadError)
        {
            failedObject = null; errorCount = 0; otherError = false; transient = false; localReadError = false;
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
                if (level == "error" || level == "critical")
                {
                    // Keep every failed object, not only local read errors: an upload can fail
                    // after the server has already stored the file (for example a dropped
                    // control connection after RNTO), and those files must be retried/verified.
                    if (row.ContainsKey("object")) failedObject = Convert.ToString(row["object"]);
                    localReadError = failedObject != null &&
                        (detail.IndexOf("failed to open source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         detail.IndexOf("failed to read source", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!aggregate.Success && !detail.StartsWith("Failed to copy", StringComparison.OrdinalIgnoreCase) && !row.ContainsKey("object")) otherError = true;
                }
                if (row.ContainsKey("object")) detail = Convert.ToString(row["object"]) + " — " + detail;
                transient = IsTransientMessage(detail);
                return detail;
            }
            catch { otherError = true; transient = IsTransientMessage(line); return line; }
        }
        private string Run(Config c, IList<string> args, int timeout, bool credentials, string input, bool progress)
        {
            CheckCancelled();
            ProcessStartInfo psi = StartInfo(c, args, credentials);
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            psi.RedirectStandardInput = input != null;
            StringBuilder output = new StringBuilder(), errors = new StringBuilder(); bool tooLarge = false, partialRenameDenied = false;
            List<string> failedObjects = new List<string>(), localReadObjects = new List<string>(), succeededObjects = new List<string>();
            int reportedErrors = 0; bool hasOtherErrors = false, transientError = false;
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
                    // Parse structured data before redaction. A valid password may
                    // itself be a JSON key (for example "object") or part of a filename.
                    string succeeded = SuccessObject(e.Data);
                    string failedObject; int errorCount; bool otherError, transient, localRead;
                    string detail = ErrorDetail(e.Data, out failedObject, out errorCount, out otherError, out transient, out localRead);
                    lock (errors)
                    {
                        if (!string.IsNullOrEmpty(succeeded) && !succeededObjects.Contains(succeeded)) succeededObjects.Add(succeeded);
                        if (!string.IsNullOrEmpty(failedObject) && !failedObjects.Contains(failedObject)) failedObjects.Add(failedObject);
                        if (localRead && !string.IsNullOrEmpty(failedObject) && !localReadObjects.Contains(failedObject)) localReadObjects.Add(failedObject);
                        reportedErrors = Math.Max(reportedErrors, errorCount); hasOtherErrors |= otherError; transientError |= transient;
                        if (detail != null && IsPartialRenameDenied(detail))
                            partialRenameDenied = true;
                        if (!string.IsNullOrEmpty(detail))
                        {
                            errors.AppendLine(Safe(detail, c));
                            if (errors.Length > 4000) errors.Remove(0, errors.Length - 4000);
                        }
                    }
                    if (progress) OnProgress(e.Data, c);
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
                    if (p.ExitCode != 0) throw new RcloneFailure(p.ExitCode, errors.ToString().Trim(), partialRenameDenied, failedObjects, localReadObjects, succeededObjects, reportedErrors, hasOtherErrors, transientError);
                    return output.ToString();
                }
                finally
                {
                    lock (processLock) { if (active == p) active = null; }
                    try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
                }
            }
        }
        private void RunWithRetry(Config c, IList<string> args, int timeout, int attempts)
        {
            RunWithRetryCapture(c, args, timeout, attempts);
        }
        private string RunWithRetryCapture(Config c, IList<string> args, int timeout, int attempts)
        {
            for (int i = 0; ; i++)
            {
                try { return Run(c, args, timeout, true, null, false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    RcloneFailure failure = ex as RcloneFailure;
                    string message = ex.Message.ToLowerInvariant();
                    if (message.IndexOf("login incorrect", StringComparison.Ordinal) >= 0 ||
                        message.IndexOf("authentication", StringComparison.Ordinal) >= 0 ||
                        message.IndexOf("530 ", StringComparison.Ordinal) >= 0 ||
                        message.IndexOf("invalid username", StringComparison.Ordinal) >= 0 ||
                        message.IndexOf("password", StringComparison.Ordinal) >= 0) throw;
                    bool retry = ex is TimeoutException || IsTransientMessage(ex.Message) || (failure != null && failure.Transient);
                    if (!retry || i >= attempts - 1) throw;
                    Say("手机 FTP 连接中断，2 秒后重试" + (i + 2) + "/" + attempts + "…");
                    Thread.Sleep(2000);
                }
            }
        }
        private void OnProgress(string line, Config c)
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
                Say(Safe(msg, c));
            }
            catch { Say(Safe(line, c)); }
        }
        private static List<string> NetworkArgs(params string[] args)
        {
            List<string> list = new List<string>(args);
            list.AddRange(new string[] {"--contimeout", "8s", "--timeout", "30s", "--ftp-idle-timeout", "30s", "--retries", "1", "--low-level-retries", "2"});
            return list;
        }
        private static List<string> NetworkArgs(int ftpConcurrency, params string[] args)
        {
            List<string> list = NetworkArgs(args);
            // FTPShare/Apache FtpServer accepts a limited number of control connections.
            // Cap the pool so a large scan or a 2-4 way upload cannot exhaust it.
            if (ftpConcurrency > 0) { list.Add("--ftp-concurrency"); list.Add(ftpConcurrency.ToString(CultureInfo.InvariantCulture)); }
            return list;
        }
        public void Test(Config c)
        {
            c.Validate(false);
            RunWithRetry(c, NetworkArgs(2, "lsf", c.Target, "--max-depth", "1"), 45000, 3);
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
                throw new Exception("当前程序以低完整性级别运行，Windows 禁止它创建盘符。请关闭工具，在普通 Windows PowerShell 中重新运行 src\\MagicTray\\build.ps1，修复成品文件的完整性标签；无需以管理员身份运行。");
            if (HasMount)
            {
                if (!MountMatches(c)) throw new Exception("已有其他配置的挂载，请先断开再更改连接设置。");
                if (!DriveOccupied(c.Drive)) throw new Exception("挂载进程存在但盘符未就绪，请断开后重新连接。");
                Test(c); return;
            }
            if (DriveOccupied(c.Drive)) throw new Exception(c.Drive + " 已被占用（可能是旧版挂载），请选择其他盘符或先卸载旧盘。");
            if (!File.Exists(@"C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll") && !File.Exists(@"C:\Program Files\WinFsp\bin\winfsp-x64.dll"))
                throw new Exception("尚未安装 WinFsp。请在“详细信息”页打开 WinFsp 官方下载链接，安装后再挂载。");
            Test(c);
            string cache = Path.Combine(LogDir, "cache"); Directory.CreateDirectory(cache);
            string volume = "MagicTray-" + Identity(c).Substring(0, 10) + "-" + c.Drive[0];
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
            try { Config.AtomicWrite(SkippedReportPath, text.ToString()); }
            catch (Exception ex) { Say("跳过报告写入失败（不影响已上传文件）：" + Safe(FirstLine(ex.Message), null)); }
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
        private int RemoveDeniedSources(string source, RcloneFailure failure, Dictionary<string, string> snapshot, List<string> pending)
        {
            int removed = 0;
            HashSet<string> selected = new HashSet<string>(pending, StringComparer.Ordinal);
            foreach (string item in failure.LocalReadObjects)
            {
                // Only exact objects selected from this source are eligible. Remote errors
                // alone are never permissions skips; the local read must also be denied now.
                string relative = NormalizeObject(item);
                if (!snapshot.ContainsKey(relative)) relative = NormalizeFailedObject(item);
                if (!snapshot.ContainsKey(relative) || !selected.Contains(relative)) continue;
                string path = Path.Combine(source, relative.Replace('/', '\\'));
                try { using (Stream stream = OpenLocalRead(path)) { } }
                catch (Exception ex)
                {
                    if (!LocalPermissionDenied(ex)) continue;
                    Skip(path, "上传时本地读取权限被拒绝，重新检查确认无法读取");
                    snapshot.Remove(relative); pending.Remove(relative); selected.Remove(relative); removed++;
                }
            }
            return removed;
        }
        private static readonly Regex PartialSuffix = new Regex(@"\.[0-9a-fA-F]{8}(?:\.m[0-9a-fA-F]{6})?\.partial$|\.partial$", RegexOptions.Compiled);
        private static bool OwnedPartial(string name, string suffix, Dictionary<string, string> snapshot)
        {
            // Do not infer ownership from the generic rclone .partial extension.
            // Restrict cleanup to our source/target marker and a current source file.
            if (snapshot.ContainsKey(name) || !name.EndsWith(suffix, StringComparison.Ordinal)) return false;
            string stem = name.Substring(0, name.Length - suffix.Length);
            Match hash = Regex.Match(stem, @"\.[0-9a-fA-F]{8}$");
            return hash.Success && snapshot.ContainsKey(stem.Substring(0, hash.Index));
        }
        private static string NormalizeObject(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Replace('\\', '/');
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
            return name.TrimStart('/');
        }
        private static string NormalizeFailedObject(string name)
        {
            return PartialSuffix.Replace(NormalizeObject(name), "");
        }
        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int cut = text.IndexOfAny(new char[] { '\r', '\n' });
            return (cut < 0 ? text : text.Substring(0, cut)).Trim();
        }
        private static List<string> FilterNames(IEnumerable<string> names, List<string> pending)
        {
            HashSet<string> allowed = new HashSet<string>(pending, StringComparer.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> result = new List<string>();
            foreach (string name in names)
            {
                string value = NormalizeObject(name);
                if (!allowed.Contains(value)) value = NormalizeFailedObject(name);
                if (value.Length > 0 && allowed.Contains(value) && seen.Add(value)) result.Add(value);
            }
            return result;
        }
        private static void MarkUploaded(IEnumerable<string> names, List<string> pending, HashSet<string> uploaded)
        {
            foreach (string name in names)
            {
                string value = NormalizeObject(name);
                if (value.Length > 0) uploaded.Add(value);
            }
            pending.RemoveAll(delegate(string item) { return uploaded.Contains(item); });
        }
        private string EnsureBackupTarget(Config c)
        {
            // rclone mkdir may report 550 for a concurrent MKD race and can still exit 0
            // with the directory absent. Verify with a listing and retry the pair.
            Say("检查手机端已有备份…");
            Exception last = null;
            for (int i = 0; i < 3; i++)
            {
                try { Run(c, NetworkArgs(2, "mkdir", c.BackupTarget), 45000, true, null, false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
                try
                {
                    return RunWithRetryCapture(c, NetworkArgs(2, "lsjson", c.BackupTarget, "--recursive", "--files-only", "--checkers", "1"), 300000, 2);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    if (i < 2) { Say("手机端备份目录尚未就绪，正在重试…"); Thread.Sleep(1000); }
                }
            }
            throw last;
        }
        private void CleanupPartialFiles(Config c, List<string> partials)
        {
            if (partials.Count == 0) return;
            string file = Path.Combine(LogDir, "cleanup-partials-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllLines(file, partials.ToArray(), new UTF8Encoding(false));
                Run(c, NetworkArgs(2, "delete", c.BackupTarget, "--files-from-raw", file, "--checkers", "1"), 300000, true, null, false);
                Say("已清理手机端 " + partials.Count + " 个上传临时文件。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Say("清理手机端临时文件失败（不影响本轮备份）：" + Safe(FirstLine(ex.Message), c)); }
            finally { try { if (File.Exists(file)) File.Delete(file); } catch { } }
        }
        private bool ReconcileRemote(Config c, Dictionary<string, string> snapshot, List<string> pending, HashSet<string> uploaded, HashSet<string> provisional)
        {
            // Some FTP servers (notably Android/FTPShare under load) close the control
            // connection after RNTO or return an error after the bytes were already stored.
            // rclone then exits nonzero even though the object is complete. Trust the
            // server's actual directory listing, not only the process exit code.
            Dictionary<string, long> remote;
            try
            {
                string listing = RunWithRetryCapture(c, NetworkArgs(2, "lsjson", c.BackupTarget, "--recursive", "--files-only", "--checkers", "1"), 600000, 3);
                remote = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (Dictionary<string, object> entry in Json().Deserialize<List<Dictionary<string, object>>>(listing))
                    remote[NormalizeObject(Convert.ToString(entry["Path"]))] = Convert.ToInt64(entry["Size"]);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Say("无法核对手机端文件：" + Safe(ex.Message, c)); return false; }
            int verified = 0;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                string item = pending[i], value;
                if (!snapshot.TryGetValue(item, out value)) { pending.RemoveAt(i); continue; }
                long expected = long.Parse(value.Substring(0, value.IndexOf(':')), CultureInfo.InvariantCulture), size;
                if (remote.TryGetValue(item, out size) && size == expected) { uploaded.Add(item); provisional.Add(item); pending.RemoveAt(i); verified++; }
            }
            if (verified > 0) Say("已核对手机端文件大小，确认 " + verified + " 个待传文件已写入。");
            return pending.Count == 0;
        }
        public void Backup(Config c)
        {
            string listFile = Path.Combine(LogDir, "backup-files-" + Guid.NewGuid().ToString("N") + ".txt");
            bool reportStarted = false; string reportState = "备份失败";
            Stopwatch totalClock = Stopwatch.StartNew(), phaseClock = new Stopwatch();
            string phase = "准备";
            try
            {
                LastUploaded = 0; LastPending = 0; LastProvisional = 0;
                c.Validate(true);
                string source = c.Source;
                string app = Path.GetFullPath(Root).TrimEnd('\\') + "\\";
                if (app.StartsWith(source, StringComparison.OrdinalIgnoreCase) || source.StartsWith(app, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("请选择工具目录以外的源文件夹，避免把密码配置、缓存和备份日志上传到手机。");
                skipped.Clear(); skippedPaths.Clear(); SkippedCount = 0;
                skippedSource = source; skippedStarted = DateTime.Now; reportStarted = true;
                SaveSkippedReport("正在运行");
                Say("准备备份：" + source + " → " + c.BackupTarget);
                phase = "FTP 连接"; phaseClock.Start(); Test(c);
                Say("阶段耗时：FTP 连接 " + phaseClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒。");
                phase = "本地摘要扫描"; phaseClock.Restart();
                Say("扫描电脑文件内容，检测新增和修改…");
                Dictionary<string, string> snapshot = Scan(source);
                Say("阶段耗时：本地摘要扫描 " + phaseClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒；文件 " + snapshot.Count + " 个。");
                phase = "手机清单检查"; phaseClock.Restart();
                SaveSkippedReport("扫描完成，正在检查备份");
                string key = Hash(Identity(c) + "\n" + source.ToUpperInvariant() + "\n" + c.Destination);
                string manifest = Path.Combine(LogDir, "backup-manifest-" + key + ".json");
                // rclone caps this suffix at 16 characters.
                string partialSuffix = ".m" + key.Substring(0, 6).ToLowerInvariant() + ".partial";
                Dictionary<string, string> previous = new Dictionary<string, string>();
                if (File.Exists(manifest)) try { previous = Json().Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest)); } catch { Say("上次扫描记录不可读，将重新上传全部源文件。"); }
                string listing = EnsureBackupTarget(c);
                Dictionary<string, long> remote = new Dictionary<string, long>(StringComparer.Ordinal);
                List<string> stalePartials = new List<string>();
                foreach (Dictionary<string, object> entry in Json().Deserialize<List<Dictionary<string, object>>>(listing))
                {
                    string raw = Convert.ToString(entry["Path"]);
                    string path = NormalizeObject(raw);
                    if (OwnedPartial(path, partialSuffix, snapshot)) { stalePartials.Add(path); continue; }
                    remote[path] = Convert.ToInt64(entry["Size"]);
                }
                CleanupPartialFiles(c, stalePartials);
                List<string> changed = new List<string>();
                foreach (KeyValuePair<string, string> file in snapshot)
                {
                    string old; long size;
                    long expected = long.Parse(file.Value.Substring(0, file.Value.IndexOf(':')), CultureInfo.InvariantCulture);
                    if (!previous.TryGetValue(file.Key, out old) || old != file.Value || !remote.TryGetValue(file.Key, out size) || size != expected) changed.Add(file.Key);
                }
                Say("阶段耗时：手机清单检查 " + phaseClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒。");
                phase = "上传与重试"; phaseClock.Restart();
                Say("本轮检查 " + snapshot.Count + " 个文件，需要上传 " + changed.Count + " 个。");
                int changedTotal = changed.Count;
                HashSet<string> changedSet = new HashSet<string>(changed, StringComparer.Ordinal);
                HashSet<string> uploaded = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> provisional = new HashSet<string>(StringComparer.Ordinal);
                List<string> pending = new List<string>(changed);
                string copyError = null; int deniedTotal = 0;
                if (pending.Count > 0)
                {
                    string versions = c.BackupTarget + "_versions/" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    bool direct = false; int attempt = 0; List<string> retry = null;
                    while (pending.Count > 0)
                    {
                        CheckCancelled();
                        List<string> batch = retry == null ? new List<string>(pending) : FilterNames(retry, pending);
                        if (batch.Count == 0) batch = new List<string>(pending);
                        retry = null;
                        File.WriteAllLines(listFile, batch, new UTF8Encoding(false));
                        int parallel = attempt == 0 ? c.BackupTransfers : 1;
                        string attemptVersions = versions + (attempt == 0 ? "" : "-retry-" + attempt) + (direct ? "-compat" : "");
                        List<string> copyArgs = NetworkArgs(parallel * 2 + 1, "copy", source, c.BackupTarget, "--backup-dir", attemptVersions, "--files-from-raw", listFile, "--ignore-times",
                            "--partial-suffix", partialSuffix, "--transfers", parallel.ToString(CultureInfo.InvariantCulture), "--checkers", parallel.ToString(CultureInfo.InvariantCulture), "--stats", "1s", "--stats-log-level", "INFO", "--use-json-log", "--log-level", "INFO");
                        if (direct) copyArgs.Add("--inplace");
                        try
                        {
                            Run(c, copyArgs, 0, true, null, true);
                            MarkUploaded(batch, pending, uploaded);
                            continue;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (RcloneFailure failure)
                        {
                            // rclone can exit nonzero after a file has already been stored (for
                            // example a control connection dropped after RNTO). Count per-object
                            // success first, then retry only the files that are actually missing.
                            MarkUploaded(failure.SucceededObjects, pending, uploaded);
                            int denied = RemoveDeniedSources(source, failure, snapshot, pending);
                            deniedTotal += denied;
                            if (pending.Count == 0)
                            {
                                if (denied > 0) Say("所有待传文件均已确认本地读取权限被拒绝，已记入跳过清单。");
                                else Say("rclone 报告了服务器错误，但本轮待传文件均已确认写入手机，按完成处理。");
                                break;
                            }
                            bool compatibility = failure.PartialRenameDenied && !direct;
                            bool transient = failure.Transient;
                            retry = FilterNames(failure.FailedObjects, pending);
                            if (retry.Count == 0) retry = new List<string>(pending);
                            if (denied > 0) Say("已跳过本地权限变更的 " + denied + " 个文件，继续备份其余可读文件。");
                            if (compatibility)
                            {
                                direct = true;
                                Say("手机 FTP 拒绝临时文件改名，正在用直接写入兼容模式重试本轮文件；原有旧版本继续保留。");
                            }
                            if (!compatibility && !transient && denied == 0)
                            {
                                if (ReconcileRemote(c, snapshot, pending, uploaded, provisional)) { Say("服务器报告错误，但手机端文件核对一致，按完成处理。"); break; }
                                copyError = Safe(FirstLine(failure.Message), c);
                                break;
                            }
                            attempt++;
                            if (attempt > 3)
                            {
                                if (ReconcileRemote(c, snapshot, pending, uploaded, provisional)) { Say("服务器报告错误，但手机端文件核对一致，按完成处理。"); break; }
                                copyError = Safe(FirstLine(failure.Message), c);
                                break;
                            }
                            Say("服务器返回错误，正在降为单路上传并重试 " + retry.Count + " 个未完成文件…");
                        }
                        catch (Exception ex)
                        {
                            if (ReconcileRemote(c, snapshot, pending, uploaded, provisional)) { Say("上传被中断，但手机端文件核对一致，按完成处理。"); break; }
                            copyError = Safe(FirstLine(ex.Message), c);
                            break;
                        }
                    }
                }
                int uploadedCount = 0;
                foreach (string item in changedSet) if (uploaded.Contains(item)) uploadedCount++;
                Say("阶段耗时：上传与重试 " + phaseClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒；上传并发 " + c.BackupTransfers + "。");
                phase = "保存结果"; phaseClock.Restart();
                // Persist confirmed progress, not only a fully successful round: a dropped
                // phone FTP connection must not make the next run re-upload everything.
                Dictionary<string, string> recorded = new Dictionary<string, string>(StringComparer.Ordinal);
                int provisionalCount = 0;
                foreach (KeyValuePair<string, string> file in snapshot)
                {
                    if (!changedSet.Contains(file.Key)) { recorded[file.Key] = file.Value; continue; }
                    if (!uploaded.Contains(file.Key)) continue;
                    if (provisional.Contains(file.Key)) { recorded[file.Key] = "?" + file.Value; provisionalCount++; }
                    else recorded[file.Key] = file.Value;
                }
                LastUploaded = uploadedCount; LastPending = pending.Count; LastProvisional = provisionalCount;
                // A wholly failed upload must not look like a successful scan next time.
                if (uploadedCount > 0 || pending.Count == 0)
                {
                    try { Config.AtomicWrite(manifest, Json().Serialize(recorded)); }
                    catch (Exception ex) { Say("本轮上传结果已确认，但本地扫描记录写入失败：" + Safe(FirstLine(ex.Message), c)); }
                }
                if (pending.Count > 0)
                    throw new Exception("上传未完成：本轮 " + changedTotal + " 个待传文件中已确认 " + uploadedCount + " 个，剩余 " + pending.Count + " 个。" + (string.IsNullOrEmpty(copyError) ? "" : "服务器错误：" + copyError));
                int expectedTotal = changedTotal - deniedTotal;
                if (provisionalCount > 0)
                {
                    // Size-only confirmation cannot prove the content is current. Keep the
                    // files in the manifest with a "?" marker so the next run retries them,
                    // and tell the user instead of claiming a complete backup.
                    reportState = "部分完成，" + provisionalCount + " 个文件仅核对大小，下轮重试";
                    LastBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  部分完成" +
                        (SkippedCount > 0 ? "（" + SkippedCount + " 项跳过）" : "") +
                        "：检查 " + snapshot.Count + "，上传 " + uploadedCount + " / " + expectedTotal + " 个文件，其中 " + provisionalCount + " 个仅按手机端大小核对，下一轮会重新上传确认。";
                }
                else
                {
                    reportState = SkippedCount == 0 ? "备份完成" : (snapshot.Count == 0 ? "未备份任何文件" : "备份完成，" + SkippedCount + " 项跳过（未备份）");
                    LastBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                        (SkippedCount == 0 ? "备份成功" : (snapshot.Count == 0 ? "未备份任何文件，" + SkippedCount + " 项跳过（未备份）" : reportState)) +
                        "：检查 " + snapshot.Count + "，上传 " + uploadedCount + " / " + expectedTotal + " 个文件。";
                }
                try { SaveResult(); } catch (Exception ex) { Say("备份已完成，但本地结果写入失败：" + Safe(FirstLine(ex.Message), c)); }
                Say(LastBackup);
            }
            catch (Exception ex)
            {
                bool partial = !(ex is OperationCanceledException) && LastUploaded > 0;
                reportState = ex is OperationCanceledException ? "已停止，扫描或上传未完成" : (partial ? "部分完成，上传未全部完成" : "备份失败，扫描或上传未完成");
                LastBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (ex is OperationCanceledException ? "  已停止" : (partial ? "  部分完成" : "  备份失败")) +
                    (LastPending > 0 ? "：已上传 " + LastUploaded + " / " + (LastUploaded + LastPending) + "，剩余 " + LastPending + " 个；" : "：") + Safe(FirstLine(ex.Message), c);
                if (reportStarted && SkippedCount > 0) LastBackup += "（另有 " + SkippedCount + " 项跳过，详见跳过项目）";
                try { SaveResult(); } catch (Exception save) { Say("备份结果已生成，但本地结果写入失败：" + Safe(FirstLine(save.Message), c)); }
                throw;
            }
            finally
            {
                if (File.Exists(listFile)) try { File.Delete(listFile); } catch { }
                if (reportStarted)
                {
                    SaveSkippedReport(reportState);
                    Say("本轮总耗时 " + totalClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒；结束阶段：" + phase + "；该阶段 " + phaseClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒。");
                }
            }
        }
        private void SaveResult() { Config.AtomicWrite(Path.Combine(LogDir, "backup-result.txt"), LastBackup); }
        public void Dispose() { Cancel(); if (mount != null) mount.Dispose(); }
    }
}
