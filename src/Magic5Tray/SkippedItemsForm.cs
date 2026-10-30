using System.Drawing;
using System.Windows.Forms;

namespace Magic5Tray
{
    internal sealed class SkippedItemsForm : Form
    {
        public SkippedItemsForm(string report, int count)
        {
            Text = "最近备份 · 跳过项目";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(820, 550); MinimumSize = new Size(580, 400);
            BackColor = Color.FromArgb(245, 247, 250); ForeColor = Color.FromArgb(30, 41, 59);
            StartPosition = FormStartPosition.CenterParent;
            TableLayoutPanel page = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 16), RowCount = 4, ColumnCount = 1 };
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(page);
            page.Controls.Add(new Label { Text = count + " 项未备份", AutoSize = true, Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), ForeColor = Color.FromArgb(180, 103, 16), Margin = new Padding(0, 0, 0, 8) }, 0, 0);
            Label hint = new Label { Text = "下方列出完整路径和原因。不可访问目录内的文件未扫描、未计数；权限恢复后，下轮备份会重新尝试。", AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Margin = new Padding(0, 0, 0, 16) };
            page.Controls.Add(hint, 0, 1);
            TextBox details = new TextBox { Text = report, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty, AccessibleName = "跳过项目完整报告" };
            page.Controls.Add(details, 0, 2);
            Button close = new Button { Text = "关闭(&C)", AutoSize = true, MinimumSize = new Size(92, 36), Padding = new Padding(14, 3, 14, 3), Anchor = AnchorStyles.Right, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, Margin = new Padding(0, 14, 0, 0) };
            close.FlatAppearance.BorderSize = 0;
            page.Controls.Add(close, 0, 3); AcceptButton = close; CancelButton = close;
            Shown += delegate { details.SelectionStart = 0; details.SelectionLength = 0; close.Focus(); };
            page.SizeChanged += delegate { hint.MaximumSize = new Size(System.Math.Max(200, page.ClientSize.Width - page.Padding.Horizontal), 0); };
        }
    }
}
