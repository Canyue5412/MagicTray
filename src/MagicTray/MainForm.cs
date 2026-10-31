using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MagicTray
{
    internal sealed class MainForm : Form
    {
        private Config cfg;
        private readonly Engine engine;
        private readonly TextBox host = new TextBox(), user = new TextBox(), password = new TextBox(), remote = new TextBox();
        private readonly TextBox source = new TextBox(), destination = new TextBox(), rclone = new TextBox(), log = new TextBox();
        private readonly NumericUpDown port = new NumericUpDown(), minutes = new NumericUpDown(), backupTransfers = new NumericUpDown();
        private readonly ComboBox drive = new ComboBox();
        private readonly CheckBox autoConnect = new CheckBox(), autoBackup = new CheckBox();
        private readonly Label connection = new Label(), summary = new Label(), current = new Label(), last = new Label(), next = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly TabControl tabs = new TabControl();
        private Button browse, saveConnection;
        private static readonly Color Ink = Color.FromArgb(30, 41, 59), Muted = Color.FromArgb(100, 116, 139), Blue = Color.FromArgb(37, 99, 235);
        private Button connect, disconnect, test, save, backup, stop, open, viewSkipped;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private bool busy, backingUp, checking, paused, exiting;
        private DateTime nextBackup = DateTime.MaxValue, nextCheck = DateTime.MinValue;
        private readonly List<Control> ftpInputs = new List<Control>();

        public MainForm(Config config, Engine service)
        {
            cfg = config; engine = service;
            Text = "MagicTray";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 560); MinimumSize = new Size(620, 460);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 250); ForeColor = Ink;

            TableLayoutPanel shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(shell);
            TableLayoutPanel heading = Stack(); heading.Margin = new Padding(0, 0, 0, 12);
            heading.Controls.Add(new Label { Text = "MagicTray", Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty });
            connection.AutoSize = true; connection.Dock = DockStyle.Top; connection.Margin = new Padding(0, 5, 0, 0);
            connection.ForeColor = Muted; connection.Text = "尚未连接 · 在首页填写手机 FTP 信息。";
            heading.Controls.Add(connection); shell.Controls.Add(heading, 0, 0);

            tabs.Dock = DockStyle.Fill; tabs.Margin = Padding.Empty; tabs.Padding = new Point(20, 8);
            shell.Controls.Add(tabs, 0, 1);
            BuildConnectionPage(CreatePage("FTP 连接"));
            BuildTransferPage(CreatePage("传输文件"));
            BuildDetailsPage(CreatePage("详细信息"));
            Label footer = Note("本地运行 · 密码由 Windows 加密保存 · 自动备份仅在工具运行期间生效");
            footer.Margin = new Padding(0, 8, 0, 0); shell.Controls.Add(footer, 0, 2);
            heading.SizeChanged += delegate { connection.MaximumSize = new Size(Math.Max(200, heading.ClientSize.Width), 0); };
            shell.SizeChanged += delegate { footer.MaximumSize = new Size(Math.Max(200, shell.ClientSize.Width - shell.Padding.Horizontal), 0); };
            engine.Message += delegate(string text) { Post(delegate { AddLog(text); }); };
            engine.Stats += delegate(Dictionary<string, object> s) { Post(delegate { RenderStats(s); }); };
            timer.Interval = 1000; timer.Tick += delegate { Tick(); };
            Shown += delegate
            {
                UpdateControls(); timer.Start();
                if (cfg.LoadWarning.Length > 0) AddLog(cfg.LoadWarning);
                if (engine.HasMount) connection.Text = "已恢复挂载进程 · " + engine.MountedDrive;
                if (cfg.AutoBackup) nextBackup = DateTime.Now;
                if (cfg.Password.Length > 0 && cfg.AutoConnect && !engine.HasMount) Connect();
            };
            FormClosing += ClosingForm; FormClosed += delegate { timer.Stop(); timer.Dispose(); };
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData >= (Keys.Control | Keys.D1) && keyData <= (Keys.Control | Keys.D3))
            {
                tabs.SelectedIndex = (int)keyData - (int)(Keys.Control | Keys.D1); return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }
        private TableLayoutPanel CreatePage(string title)
        {
            TabPage tab = new TabPage(title) { BackColor = BackColor, Padding = new Padding(10) };
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            TableLayoutPanel page = Stack(); scroll.Controls.Add(page); tab.Controls.Add(scroll); tabs.TabPages.Add(tab);
            page.SizeChanged += delegate { ConstrainText(page, page.ClientSize.Width); };
            return page;
        }
        private static void ConstrainText(Control parent, int width)
        {
            int inner = Math.Max(160, width - parent.Padding.Horizontal);
            foreach (Control child in parent.Controls)
            {
                Label label = child as Label;
                if (label != null && label.AutoSize) label.MaximumSize = new Size(Math.Max(160, inner - label.Margin.Horizontal), 0);
                if (child is TableLayoutPanel && ((TableLayoutPanel)child).ColumnCount == 1) ConstrainText(child, inner - child.Margin.Horizontal);
            }
        }
        private void BuildConnectionPage(TableLayoutPanel page)
        {
            TableLayoutPanel card = Card("手机 FTP 服务", null); page.Controls.Add(card);
            TableLayoutPanel fields = Grid(4, 2);
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            port.Minimum = 1; port.Maximum = 65535; port.Value = cfg.Port;
            host.Text = cfg.Host; user.Text = cfg.User; password.Text = cfg.Password; password.UseSystemPasswordChar = true;
            Field(fields, "手机 IP(&H)", host, 0, 0); Field(fields, "端口(&P)", port, 2, 0);
            Field(fields, "用户名(&U)", user, 0, 1); Field(fields, "密码(&W)", password, 2, 1); card.Controls.Add(fields);
            FlowLayoutPanel actions = Flow();
            connect = Button("连接并挂载(&C)", delegate { Connect(); }); Primary(connect);
            test = Button("测试连接(&T)", delegate { if (ReadSettings(false)) Run("正在验证 FTP 登录…", false, delegate(Config c) { engine.Test(c); }, delegate { connection.Text = "FTP 登录成功 · 可以挂载或备份"; }); });
            disconnect = Button("断开", delegate { Disconnect(); });
            open = Button("打开网盘(&O)", delegate { OpenPath(engine.MountedDrive + "\\"); });
            actions.Controls.AddRange(new Control[] { connect, test, open, disconnect }); card.Controls.Add(actions);
            FlowLayoutPanel options = Flow();
            autoConnect.Text = "启动工具时自动连接"; autoConnect.AutoSize = true; autoConnect.Checked = cfg.AutoConnect; autoConnect.Margin = new Padding(0, 10, 16, 0);
            saveConnection = Button("保存连接设置", delegate { if (ReadSettings(false)) AddLog("连接设置已保存。"); });
            options.Controls.AddRange(new Control[] { autoConnect, saveConnection }); card.Controls.Add(options);

            TableLayoutPanel advanced = Stack(); advanced.Visible = false;
            drive.DropDownStyle = ComboBoxStyle.DropDownList;
            for (char c = 'D'; c <= 'Z'; c++) drive.Items.Add(c + ":"); drive.SelectedItem = cfg.Drive;
            if (drive.SelectedIndex < 0) drive.SelectedItem = "Z:";
            remote.Text = cfg.RemotePath.Length == 0 ? "/" : cfg.RemotePath; rclone.Text = cfg.RclonePath;
            TableLayoutPanel advancedFields = Grid(4, 2);
            advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
            advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            Field(advancedFields, "手机根目录", remote, 0, 0); Field(advancedFields, "盘符", drive, 2, 0);
            Field(advancedFields, "rclone 路径", rclone, 0, 1);
            Button pick = Button("选择程序…", delegate { using (OpenFileDialog d = new OpenFileDialog { Filter = "rclone.exe|rclone.exe", Title = "选择 rclone.exe" }) if (d.ShowDialog(this) == DialogResult.OK) rclone.Text = d.FileName; });
            pick.Margin = Padding.Empty; advancedFields.Controls.Add(pick, 2, 1); advancedFields.SetColumnSpan(pick, 2);
            advanced.Controls.Add(advancedFields); advanced.Controls.Add(Note("rclone 留空时自动查找；手机目录相对于 FTP 账号根目录。"));
            card.Controls.Add(Disclosure("高级连接设置", advanced)); card.Controls.Add(advanced);
            page.Controls.Add(Note("挂载后可通过盘符访问手机；直接备份请切换到“传输文件”，无需先挂载。"));
            ftpInputs.AddRange(new Control[] { host, port, user, password, remote, drive, rclone, pick });
        }
        private void BuildTransferPage(TableLayoutPanel page)
        {
            TableLayoutPanel card = Card("电脑 → 手机备份", null); page.Controls.Add(card);
            TableLayoutPanel fields = Grid(3, 2);
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            source.Text = cfg.Source; destination.Text = cfg.Destination;
            Field(fields, "电脑文件夹(&F)", source, 0, 0);
            browse = Button("浏览…", delegate { using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "选择要上传备份的电脑文件夹", SelectedPath = source.Text }) if (d.ShowDialog(this) == DialogResult.OK) source.Text = d.SelectedPath; });
            browse.Dock = DockStyle.Fill; browse.Margin = Padding.Empty; fields.Controls.Add(browse, 2, 0);
            Field(fields, "手机备份目录", destination, 0, 1); fields.SetColumnSpan(destination, 2); card.Controls.Add(fields);
            FlowLayoutPanel actions = Flow();
            backup = Button("立即备份(&B)", delegate { StartBackup(true); }); Primary(backup);
            stop = Button("停止备份", StopBackup);
            viewSkipped = Button("查看跳过项目（" + engine.SkippedCount + "）", delegate { using (Form window = CreateSkippedWindow()) window.ShowDialog(this); });
            actions.Controls.AddRange(new Control[] { backup, stop, viewSkipped }); card.Controls.Add(actions);
            FlowLayoutPanel schedule = Flow();
            autoBackup.Text = "自动备份，每"; autoBackup.AutoSize = true; autoBackup.Checked = cfg.AutoBackup; autoBackup.Margin = new Padding(0, 10, 5, 0);
            minutes.Minimum = 1; minutes.Maximum = 1440; minutes.Value = cfg.BackupMinutes; minutes.Width = 64; minutes.Margin = new Padding(0, 7, 5, 0);
            backupTransfers.Minimum = 1; backupTransfers.Maximum = 4; backupTransfers.Value = cfg.BackupTransfers; backupTransfers.Width = 48; backupTransfers.AccessibleName = "上传并发";
            FlowLayoutPanel parallel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(6, 7, 10, 0) };
            parallel.Controls.Add(new Label { Text = "上传并发", AutoSize = true, Margin = new Padding(0, 3, 4, 0) });
            backupTransfers.Margin = Padding.Empty; parallel.Controls.Add(backupTransfers);
            save = Button("保存设置(&S)", SaveSchedule);
            schedule.Controls.AddRange(new Control[] { autoBackup, minutes, new Label { Text = "分钟扫描", AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, parallel, save }); card.Controls.Add(schedule);

            TableLayoutPanel status = Card("备份状态", null); page.Controls.Add(status);
            summary.Text = "等待备份"; summary.Font = new Font(Font, FontStyle.Bold);
            current.Text = "无需挂载，开始备份时自动保存设置。";
            if (engine.SkippedCount > 0) { summary.Text = "最近备份有 " + engine.SkippedCount + " 项跳过（未备份）"; summary.ForeColor = Color.FromArgb(180, 103, 16); }
            last.Text = engine.LastBackup; next.Text = "自动备份未启用";
            foreach (Label label in new Label[] { summary, current, last, next })
            {
                label.AutoSize = true; label.Dock = DockStyle.Top; label.Margin = new Padding(0, 2, 0, 0);
                if (label != summary) label.ForeColor = Muted;
            }
            status.Controls.Add(summary);
            progress.Dock = DockStyle.Top; progress.Height = 7; progress.Margin = new Padding(0, 4, 0, 4); status.Controls.Add(progress);
            status.Controls.Add(current); status.Controls.Add(last); status.Controls.Add(next);
        }
        private void BuildDetailsPage(TableLayoutPanel page)
        {
            TableLayoutPanel help = Card("使用与备份规则", null); page.Controls.Add(help);
            help.Controls.Add(Note("连接：开启手机 FTP 服务后填写地址和账号，挂载盘符可选。"));
            help.Controls.Add(Note("备份：只上传新增与修改文件，电脑删除不会删除手机副本。首次扫描会计算内容摘要，可能需要一些时间。"));
            help.Controls.Add(Note("性能：上传并发默认为 2，可在传输页调为 1–4；手机连接受限或不稳定时选 1。数万小文件仍受 FTP 往返和手机存储速度影响。"));
            help.Controls.Add(Note("旧版本：更新前的副本保留在手机备份目录旁的 _versions 文件夹，不自动清理。"));
            help.Controls.Add(Note("跳过项目：本地权限拒绝或目录链接会列出路径及原因；不可访问目录内部未扫描、未计数，下轮重新尝试。"));
            help.Controls.Add(Note("退出：自动备份随窗口关闭停止。保留挂载后可再次打开管理；断开前请完成网盘文件复制。"));
            TableLayoutPanel dependencies = Card("依赖与隐私", null); page.Controls.Add(dependencies);
            FlowLayoutPanel links = Flow();
            links.Controls.Add(Link("下载 rclone", "https://rclone.org/downloads/"));
            links.Controls.Add(Link("下载 WinFsp", "https://winfsp.dev/rel/"));
            links.Controls.Add(Link(".NET Framework", "https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server"));
            dependencies.Controls.Add(links);
            dependencies.Controls.Add(Note("rclone 负责传输，WinFsp 用于挂载磁盘。依赖需自行安装；rclone 可放在工具目录、tools 子目录或 PATH 中。"));
            dependencies.Controls.Add(Note("密码通过 Windows DPAPI 保存，仅当前 Windows 用户可解密。FTP 使用明文连接，请在可信局域网内使用。"));
            TableLayoutPanel about = Card("MagicTray 2.2.1 · AI 制作", null); page.Controls.Add(about);
            about.Controls.Add(Note("最初版本由 DeepSeek 制作；后续由 OpenAI Codex（GPT-6.1 Sol）辅助迭代。用户主导需求、测试与验收。程序本地运行，无需 AI API。"));
            TableLayoutPanel logArea = Stack(); logArea.Visible = false;
            log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Both; log.WordWrap = false;
            log.Dock = DockStyle.Top; log.Height = 168; log.BackColor = Color.White;
            log.BorderStyle = BorderStyle.FixedSingle; log.Font = new Font("Consolas", 9F); log.AccessibleName = "运行日志";
            logArea.Controls.Add(log); page.Controls.Add(Disclosure("运行日志", logArea)); page.Controls.Add(logArea);
        }
        private static LinkLabel Link(string text, string url)
        {
            LinkLabel link = new LinkLabel { Text = text, AutoSize = true, LinkColor = Blue, Margin = new Padding(0, 3, 22, 3) };
            link.LinkClicked += delegate { try { Process.Start(url); } catch (Exception ex) { MessageBox.Show(ex.Message, "MagicTray"); } }; return link;
        }
        private void SaveSchedule()
        {
            if (!ReadSettings(autoBackup.Checked)) return;
            nextBackup = cfg.AutoBackup ? DateTime.Now : DateTime.MaxValue; AddLog("设置已保存。");
        }
        private void StopBackup()
        {
            autoBackup.Checked = false; cfg.AutoBackup = false; nextBackup = DateTime.MaxValue;
            try { cfg.Save(); } catch (Exception ex) { AddLog(ex.Message); }
            engine.Cancel(); stop.Enabled = false; current.Text = "正在停止…";
        }
        private static TableLayoutPanel Stack()
        {
            TableLayoutPanel p = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return p;
        }
        private TableLayoutPanel Card(string title, string subtitle)
        {
            TableLayoutPanel card = Stack(); card.BackColor = Color.White; card.Padding = new Padding(16, 10, 16, 10); card.Margin = new Padding(0, 0, 0, 8);
            card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), Margin = new Padding(0, 0, 0, subtitle == null ? 0 : 3) });
            if (subtitle != null) { Label note = Note(subtitle); note.Margin = new Padding(0, 0, 0, 8); card.Controls.Add(note); }
            return card;
        }
        private static Label Note(string text) { return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 4, 0, 2) }; }
        private static TableLayoutPanel Grid(int columns, int rows)
        {
            TableLayoutPanel p = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
            for (int i = 0; i < rows; i++) p.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            return p;
        }
        private static FlowLayoutPanel Flow() { return new ActionFlow { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 4, 0, 0) }; }
        private sealed class ActionFlow : FlowLayoutPanel
        {
            public override Size GetPreferredSize(Size proposedSize)
            {
                // TableLayoutPanel can initially measure a flow row at a narrow width.
                // Measure against the card width so a wrapped row shrinks again on resize.
                int width = Parent == null ? proposedSize.Width : Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal;
                return base.GetPreferredSize(new Size(Math.Max(1, width), 0));
            }
        }
        private static void Field(TableLayoutPanel p, string name, Control input, int x, int y)
        {
            p.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, x, y);
            input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 5, 12, 5); p.Controls.Add(input, x + 1, y);
        }
        private static Button Button(string text, Action action)
        {
            Button b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 34), Padding = new Padding(12, 3, 12, 3), Margin = new Padding(0, 2, 8, 2), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(218, 225, 234);
            b.Click += delegate { action(); }; return b;
        }
        private static void Primary(Button button)
        {
            button.BackColor = Blue; button.ForeColor = Color.White; button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(29, 78, 216); button.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 64, 175);
        }
        private static Button Disclosure(string title, Control content)
        {
            Button toggle = Button("+  " + title, delegate { });
            toggle.FlatAppearance.BorderSize = 0; toggle.ForeColor = Blue; toggle.BackColor = Color.Transparent;
            toggle.Padding = Padding.Empty; toggle.MinimumSize = new Size(0, 28); toggle.Margin = Padding.Empty;
            toggle.AccessibleName = title + "，已折叠";
            toggle.Click += delegate { content.Visible = !content.Visible; toggle.Text = (content.Visible ? "−  " : "+  ") + title; toggle.AccessibleName = title + (content.Visible ? "，已展开" : "，已折叠"); };
            return toggle;
        }
        private void Post(Action action) { if (!IsDisposed && IsHandleCreated) try { BeginInvoke(action); } catch (InvalidOperationException) { } }
        private void AddLog(string text)
        {
            if (log.TextLength > 48000) log.Text = log.Text.Substring(log.TextLength - 32000);
            log.AppendText(text + Environment.NewLine);
        }
        private void OpenPath(string path) { try { Process.Start("explorer.exe", Engine.Quote(path)); } catch (Exception ex) { ShowError(ex.Message); } }
        private void ShowError(string message) { MessageBox.Show(this, message, "MagicTray", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        internal Form CreateSkippedWindow() { return new SkippedItemsForm(engine.ReadSkippedReport(), engine.SkippedCount); }
        private bool ReadSettings(bool forBackup)
        {
            try
            {
                Config c = cfg.Snapshot(); c.Host = host.Text.Trim(); c.User = user.Text.Trim(); c.Password = password.Text;
                c.Port = (int)port.Value; c.Drive = Convert.ToString(drive.SelectedItem); c.RemotePath = remote.Text;
                c.RclonePath = rclone.Text.Trim(); c.Source = source.Text.Trim(); c.Destination = destination.Text.Trim();
                c.BackupTransfers = (int)backupTransfers.Value;
                c.AutoConnect = autoConnect.Checked; c.AutoBackup = autoBackup.Checked; c.BackupMinutes = (int)minutes.Value;
                c.Validate(forBackup); c.Save(); cfg = c; return true;
            }
            catch (Exception ex) { ShowError(ex.Message); return false; }
        }
        private void Run(string title, bool isBackup, Action<Config> action, Action completed)
        {
            if (busy) return;
            busy = true; backingUp = isBackup; engine.ResetCancellation(); UpdateControls();
            if (isBackup) { summary.Text = title; summary.ForeColor = Color.DarkSlateGray; progress.Value = 0; progress.Style = ProgressBarStyle.Marquee; current.Text = "正在准备…"; }
            else connection.Text = title;
            Config snapshot = cfg.Snapshot();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try { action(snapshot); } catch (Exception ex) { error = ex; }
                Post(delegate
                {
                    busy = false; backingUp = false;
                    if (isBackup)
                    {
                        progress.Style = ProgressBarStyle.Blocks; progress.Value = error == null ? 100 : 0;
                        summary.Text = error == null ? (engine.LastProvisional > 0 ? "本轮部分完成：" + engine.LastProvisional + " 个文件仅核对大小，下一轮重试" : (engine.SkippedCount == 0 ? "本轮备份完成" : "备份完成，" + engine.SkippedCount + " 项跳过（未备份）")) : (error is OperationCanceledException ? "备份已停止" : (engine.LastPending > 0 ? "备份未完成：已上传 " + engine.LastUploaded + " 个，剩余 " + engine.LastPending + " 个" + (engine.SkippedCount > 0 ? "，跳过 " + engine.SkippedCount + " 个" : "") : "备份失败，请查看“详细信息”页日志"));
                        if (error == null && engine.LastBackup.Contains("未备份任何文件")) summary.Text = "未备份任何文件，" + engine.SkippedCount + " 项跳过（未备份）";
                        summary.ForeColor = error != null ? Color.Firebrick : (engine.SkippedCount > 0 || engine.LastProvisional > 0 ? Color.DarkOrange : Color.DarkSlateGray);
                        current.Text = "当前无文件传输"; last.Text = engine.LastBackup;
                        nextBackup = cfg.AutoBackup ? DateTime.Now.AddMinutes(cfg.BackupMinutes) : DateTime.MaxValue;
                    }
                    if (error != null)
                    {
                        AddLog(engine.Safe(error.Message, snapshot));
                        if (!isBackup) { paused = true; connection.Text = "连接操作失败 · 详见“详细信息”页的运行日志"; ShowError(engine.Safe(error.Message, snapshot)); }
                    }
                    else if (completed != null) completed();
                    UpdateControls();
                });
            });
        }
        private void Connect()
        {
            if (busy || !ReadSettings(false)) return;
            paused = false;
            Run("正在验证账号并挂载…", false, delegate(Config c) { engine.Mount(c); }, delegate { connection.Text = "已挂载 " + cfg.Drive + " · " + cfg.Host + ":" + cfg.Port; });
        }
        private void Disconnect()
        {
            if (busy) return;
            if (MessageBox.Show(this, "请确认通过网盘进行的文件复制已经完成。现在断开挂载？", "断开网盘", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            paused = true; autoBackup.Checked = false; cfg.AutoBackup = false; nextBackup = DateTime.MaxValue;
            try { cfg.Save(); } catch (Exception ex) { ShowError(ex.Message); return; }
            Run("正在断开…", false, delegate { engine.Unmount(); }, delegate { connection.Text = "已断开 · 自动连接和备份已暂停"; });
        }
        private void StartBackup(bool manual)
        {
            if (busy) return;
            if (manual && !ReadSettings(true)) return;
            Run("正在扫描并上传备份…", true, delegate(Config c) { engine.Backup(c); }, null);
        }
        private void UpdateControls()
        {
            bool mounted = engine.HasMount;
            foreach (Control c in ftpInputs) c.Enabled = !busy && !mounted;
            connect.Enabled = !busy && !mounted; test.Enabled = !busy; disconnect.Enabled = !busy && mounted;
            open.Enabled = mounted && Engine.DriveOccupied(engine.MountedDrive);
            foreach (Control c in new Control[] { source, destination, autoBackup, minutes, backupTransfers, autoConnect, saveConnection, save, backup }) c.Enabled = !busy;
            // Disable the folder picker as well; keep the stop action available while uploading/scanning.
            browse.Enabled = !busy;
            stop.Enabled = backingUp;
            viewSkipped.Text = "查看跳过项目（" + engine.SkippedCount + "）"; viewSkipped.Enabled = !busy;
        }
        private void Tick()
        {
            next.Text = cfg.AutoBackup ? (backingUp ? "本轮结束后安排下一次扫描" : "下次自动扫描：" + (nextBackup == DateTime.MaxValue ? "等待保存设置" : nextBackup.ToString("HH:mm:ss"))) : "自动备份未启用 · 修改后点击“保存设置”生效";
            if (busy) return;
            if (cfg.AutoBackup && DateTime.Now >= nextBackup) { StartBackup(false); return; }
            if (checking || DateTime.Now < nextCheck || cfg.Password.Length == 0) return;
            checking = true; nextCheck = DateTime.Now.AddSeconds(10); Config c = cfg.Snapshot();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool online = Engine.Online(c);
                Post(delegate
                {
                    checking = false; if (busy) return;
                    bool mounted = engine.HasMount;
                    connection.Text = mounted ? (online ? "已挂载 " + engine.MountedDrive + " · 手机端口在线" : "挂载仍在 · 手机离线，访问可能失败") : (online ? "手机端口在线 · 未挂载" : "手机离线 · 检查 FTP 服务和 Wi-Fi");
                    connection.ForeColor = mounted && online ? Color.SeaGreen : Color.DarkSlateGray;
                    UpdateControls();
                    if (online && !mounted && cfg.AutoConnect && !paused) Connect();
                });
            });
        }
        private static double Number(Dictionary<string, object> s, string name) { object value; return s.TryGetValue(name, out value) && value != null ? Convert.ToDouble(value) : 0; }
        private static string FormatSize(double n)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" }; int u = 0;
            while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; } return n.ToString("0.##") + " " + units[u];
        }
        private void RenderStats(Dictionary<string, object> s)
        {
            if (!backingUp) return;
            double bytes = Number(s, "bytes"), total = Number(s, "totalBytes"), speed = Number(s, "speed");
            summary.Text = string.Format("已上传 {0} / {1} · {2}/s · 文件 {3}/{4} · 错误 {5}", FormatSize(bytes), FormatSize(total), FormatSize(speed), Number(s, "transfers"), Number(s, "totalTransfers"), Number(s, "errors"));
            progress.Style = total > 0 ? ProgressBarStyle.Blocks : ProgressBarStyle.Marquee;
            if (total > 0) progress.Value = (int)Math.Max(0, Math.Min(100, bytes * 100 / total));
            List<string> names = new List<string>(); object transfers;
            if (s.TryGetValue("transferring", out transfers) && transfers is IEnumerable)
                foreach (object obj in (IEnumerable)transfers)
                {
                    Dictionary<string, object> file = obj as Dictionary<string, object>;
                    if (file != null && file.ContainsKey("name")) names.Add(Convert.ToString(file["name"]));
                }
            current.Text = names.Count > 0 ? "正在上传：" + string.Join("；", names.ToArray()) : "正在检查文件 / 等待服务器响应…";
        }
        private void ClosingForm(object sender, FormClosingEventArgs e)
        {
            if (exiting) return;
            if (busy) { e.Cancel = true; ShowError(backingUp ? "备份正在运行，请先点击“停止备份”或等待完成。" : "连接操作正在进行，请等待完成后退出。"); return; }
            if (!engine.HasMount) return;
            DialogResult result = MessageBox.Show(this, "退出时断开挂载吗？请先完成网盘文件复制。\r\n\r\n是：断开后退出\r\n否：保留挂载退出（自动备份会停止）", "退出网盘工具", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel) { e.Cancel = true; return; }
            if (result == DialogResult.No) return;
            e.Cancel = true;
            Run("正在断开并退出…", false, delegate { engine.Unmount(); }, delegate { exiting = true; Close(); });
        }
    }
}

