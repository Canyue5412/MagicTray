using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MagicTray
{
    internal sealed class Config
    {
        internal const string RemoteName = "magictray";
        public string Host = "", User = "", Password = "", Drive = "Z:";
        public string RemotePath = "/", Source = "", Destination = "Backup/PC", RclonePath = "";
        public int Port = 5656, BackupMinutes = 5, BackupTransfers = 2;
        public bool AutoConnect = false, AutoBackup = false;
        public string IniPath, LoadWarning = "";
        private Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Config Snapshot() { return (Config)MemberwiseClone(); }
        public static Config Load(string path)
        {
            Config c = new Config(); c.IniPath = path;
            if (!File.Exists(path)) return c;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim(); int i = line.IndexOf('=');
                if (i <= 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                c.values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            c.Host = c.Get("Host", c.Host); c.User = c.Get("User", c.User);
            c.Drive = c.Get("Drive", c.Drive); c.RclonePath = c.Get("RclonePath", "");
            c.RemotePath = c.Get("RemotePath", "/"); c.Source = c.Get("BackupSource", "");
            c.Destination = c.Get("BackupDestination", c.Destination);
            int n; if (int.TryParse(c.Get("Port", ""), out n) && n > 0 && n <= 65535) c.Port = n;
            if (int.TryParse(c.Get("BackupMinutes", ""), out n) && n >= 1 && n <= 1440) c.BackupMinutes = n;
            if (int.TryParse(c.Get("BackupTransfers", ""), out n) && n >= 1 && n <= 4) c.BackupTransfers = n;
            c.AutoConnect = c.Get("AutoConnect", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            c.AutoBackup = c.Get("AutoBackup", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            string secret = c.Get("PasswordProtected", "");
            if (secret.Length > 0)
            {
                try { c.Password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser)); }
                catch { c.LoadWarning = "保存的密码无法解密，请重新输入（密码仅限当前 Windows 用户使用）。"; }
            }
            return c;
        }
        private string Get(string k, string fallback) { string v; return values.TryGetValue(k, out v) ? v : fallback; }
        public void Save()
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            values["Host"] = Host; values["Port"] = Port.ToString(); values["User"] = User;
            values["Drive"] = Drive; values["RemotePath"] = RemotePath; values["RclonePath"] = RclonePath;
            values["AutoConnect"] = AutoConnect.ToString().ToLowerInvariant();
            values["BackupSource"] = Source; values["BackupDestination"] = Destination;
            values["BackupTransfers"] = BackupTransfers.ToString();
            values["BackupMinutes"] = BackupMinutes.ToString(); values["AutoBackup"] = AutoBackup.ToString().ToLowerInvariant();
            values["PasswordProtected"] = Password.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Password), null, DataProtectionScope.CurrentUser));
            StringBuilder b = new StringBuilder("# MagicTray settings; password protected by Windows DPAPI\r\n");
            foreach (KeyValuePair<string, string> p in values) b.AppendLine(p.Key + "=" + p.Value);
            AtomicWrite(IniPath, b.ToString());
        }
        public static void AtomicWrite(string path, string text)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string rollback = null;
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (!File.Exists(path)) { File.Move(tmp, path); return; }
                try { File.Replace(tmp, path, null); }
                catch (Exception ex)
                {
                    // Some filesystems do not implement Replace. Preserve the old state
                    // until publishing the new file succeeds; never delete the sole copy.
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is PlatformNotSupportedException)) throw;
                    rollback = path + "." + Guid.NewGuid().ToString("N") + ".rollback";
                    File.Move(path, rollback);
                    try { File.Move(tmp, path); }
                    catch
                    {
                        if (!File.Exists(path)) File.Move(rollback, path);
                        throw;
                    }
                    try { File.Delete(rollback); } catch { }
                }
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        public void Validate(bool backup)
        {
            if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host) == UriHostNameType.Unknown)
                throw new Exception("请输入手机 IP 或主机名；端口请填在右侧，不要输入 ftp:// 前缀。");
            if (Port < 1 || Port > 65535) throw new Exception("端口必须为 1–65535。");
            if (string.IsNullOrWhiteSpace(User) || Password.Length == 0) throw new Exception("请填写 FTP 用户名和密码。");
            if (User.IndexOfAny(new char[] {'\r', '\n', '\0'}) >= 0 || Password.IndexOfAny(new char[] {'\r', '\n', '\0'}) >= 0)
                throw new Exception("用户名和密码不能包含换行或空字符。");
            if (!Regex.IsMatch(Drive, "^[D-Z]:$")) throw new Exception("请选择 D: 至 Z: 的盘符。");
            RemotePath = NormalizeRemote(RemotePath, true);
            if (BackupTransfers < 1 || BackupTransfers > 4) throw new Exception("上传并发必须为 1–4；手机 FTP 连接受限时请选择 1。");
            if (!backup) return;
            if (!Path.IsPathRooted(Source)) throw new Exception("请选择存在的电脑源文件夹。");
            Source = Path.GetFullPath(Source).TrimEnd('\\') + "\\";
            if (Source.StartsWith(@"\\") || !Regex.IsMatch(Source, "^[A-Za-z]:\\\\")) throw new Exception("备份源必须是电脑本地磁盘文件夹。");
            if (Source.StartsWith(Drive + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("不能把手机挂载盘作为电脑备份源。");
            DriveInfo disk = new DriveInfo(Path.GetPathRoot(Source));
            if (disk.DriveType != DriveType.Fixed && disk.DriveType != DriveType.Removable) throw new Exception("备份源必须位于本地硬盘或移动硬盘。");
            try
            {
                FileAttributes attributes = File.GetAttributes(Source);
                if ((attributes & FileAttributes.Directory) == 0) throw new Exception("请选择存在的电脑源文件夹。");
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new Exception("请选择实际文件夹，不要选择目录链接。");
                // A normal child below a junction is still reached through a link.
                // Reject that route as well so path aliases cannot bypass the engine's
                // exclusion of its own configuration, cache and credentials.
                for (DirectoryInfo parent = new DirectoryInfo(Source).Parent; parent != null; parent = parent.Parent)
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new Exception("备份源的上级目录包含目录链接，请直接选择实际磁盘路径。");
            }
            catch (UnauthorizedAccessException) { /* Scanner records an inaccessible source root as a skipped directory. */ }
            catch (System.Security.SecurityException) { }
            catch (FileNotFoundException) { throw new Exception("请选择存在的电脑源文件夹。"); }
            catch (DirectoryNotFoundException) { throw new Exception("请选择存在的电脑源文件夹。"); }
            Destination = NormalizeRemote(Destination, false);
        }
        public static string NormalizeRemote(string path, bool allowRoot)
        {
            path = path.Trim().Replace('\\', '/').Trim('/');
            if (path.IndexOfAny(new char[] {'\r', '\n', '\0', ':'}) >= 0) throw new Exception("手机目录包含无效字符。");
            foreach (string part in path.Split('/')) if (part == "." || part == "..") throw new Exception("手机目录不能包含 . 或 ..。");
            if (path.Length == 0 && !allowRoot) throw new Exception("请指定手机备份子目录，例如 Backup/PC。");
            return path;
        }
        public string Target { get { return RemoteName + ":" + RemotePath.Trim('/'); } }
        public string BackupTarget { get { return RemoteName + ":" + (RemotePath.Trim('/').Length == 0 ? "" : RemotePath.Trim('/') + "/") + Destination.Trim('/'); } }
    }
}
