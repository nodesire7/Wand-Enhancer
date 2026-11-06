using System;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WandEnhancer.Fork.Installer
{
    internal sealed class InstallerForm : Form
    {
        private readonly Button install;
        private readonly Button openLog;
        private readonly Button openBackup;
        private readonly Label status;
        private readonly Label[] steps;
        private readonly ProgressBar progress;
        private readonly TextBox details;
        private readonly CheckBox launch;
        private bool busy;
        private bool completed;
        private string logPath;
        private string backupPath;
        private static readonly Color Background = Color.FromArgb(9, 9, 11);
        private static readonly Color Foreground = Color.FromArgb(250, 250, 250);
        private static readonly Color Muted = Color.FromArgb(174, 174, 183);

        public InstallerForm()
        {
            string version = InstallEngine.LoadPackage().version;
            Text = "GBTGAME · Wand 安装助手";
            ClientSize = new Size(740, 665);
            MinimumSize = new Size(680, 680);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10);
            BackColor = Background;
            ForeColor = Foreground;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32), ColumnCount = 1, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 45, 56, 44, 155, 32, 37 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "Wand，一次安装就绪", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(new Label { Text = "GBTGAME · Wand " + version + "\r\n自动处理旧版、安装、应用修改，最后启动。", ForeColor = Muted, Dock = DockStyle.Fill }, 0, 1);
            layout.Controls.Add(new Label { Text = "点击安装将关闭并卸载当前用户的 Wand / WeMod，清理相关配置。\r\n旧配置会先备份；此操作会退出原有登录状态。", ForeColor = Muted, Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 9) }, 0, 2);

            var stepList = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(0, 10, 0, 0) };
            steps = new Label[6];
            string[] names = { "1   校验安装文件", "2   备份旧版配置", "3   卸载与清理旧版", "4   安装 Wand " + version, "5   应用修改并验证", "6   启动 Wand" };
            for (int i = 0; i < steps.Length; i++)
            {
                stepList.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 6));
                steps[i] = new Label { Text = names[i], Dock = DockStyle.Fill, ForeColor = Muted, AutoSize = true };
                stepList.Controls.Add(steps[i], 0, i);
            }
            layout.Controls.Add(stepList, 0, 3);
            progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 6, Margin = new Padding(0, 10, 0, 8), AccessibleName = "安装进度" };
            layout.Controls.Add(progress, 0, 4);
            status = new Label { Text = "准备就绪，点击“安装”开始。", Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(0, 6, 0, 0), AccessibleName = "当前状态" };
            layout.Controls.Add(status, 0, 5);
            details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 27), ForeColor = Muted, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "安装日志", TabIndex = 5 };
            layout.Controls.Add(details, 0, 6);
            launch = new CheckBox { Text = "完成后启动 Wand", Checked = true, AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 8, 0, 0), TabIndex = 1 };
            layout.Controls.Add(launch, 0, 7);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 9, 0, 0) };
            install = MakeButton("安装", true);
            install.TabIndex = 0;
            install.Click += async (sender, args) => await BeginInstall();
            openLog = MakeButton("查看日志", false);
            openLog.Enabled = false;
            openLog.Visible = false;
            openLog.Click += (sender, args) => Open(logPath);
            openBackup = MakeButton("打开备份", false);
            openBackup.Enabled = false;
            openBackup.Visible = false;
            openBackup.Click += (sender, args) => Open(backupPath);
            actions.Controls.Add(install);
            actions.Controls.Add(openLog);
            actions.Controls.Add(openBackup);
            layout.Controls.Add(actions, 0, 8);
            AcceptButton = install;
            FormClosing += (sender, args) => { if (busy) { args.Cancel = true; status.Text = "正在处理安装，请等待当前流程结束。"; } };
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button { Text = text, Size = new Size(110, 36), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Foreground : Background, ForeColor = primary ? Background : Foreground,
                Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
            return button;
        }

        private async Task BeginInstall()
        {
            if (completed) { Close(); return; }
            busy = true;
            install.Enabled = false;
            launch.Enabled = false;
            openLog.Enabled = openBackup.Enabled = false;
            details.Clear();
            bool startAfter = launch.Checked;
            using (var engine = new InstallEngine((step, text) => BeginInvoke(new Action(() => UpdateProgress(step, text)))))
            {
                try
                {
                    await Task.Run(() => engine.Install(startAfter));
                    completed = true;
                    install.Text = "完成";
                }
                catch (Exception error)
                {
                    engine.RecordFailure(error);
                    status.Text = "安装未完成：" + error.Message;
                    status.ForeColor = Color.FromArgb(255, 171, 160);
                    details.AppendText("\r\n安装已停止：" + error.Message + "\r\n可查看日志，排除问题后点击重试。\r\n");
                    install.Text = "重试安装";
                }
                finally
                {
                    logPath = engine.LogPath;
                    backupPath = engine.BackupPath;
                    busy = false;
                    install.Enabled = true;
                    launch.Enabled = !completed;
                    openLog.Enabled = File.Exists(logPath);
                    openBackup.Enabled = Directory.Exists(backupPath);
                    openLog.Visible = openLog.Enabled;
                    openBackup.Visible = openBackup.Enabled;
                }
            }
        }

        private void UpdateProgress(int step, string text)
        {
            progress.Value = Math.Min(step, 6);
            status.Text = text;
            status.ForeColor = Foreground;
            for (int i = 0; i < steps.Length; i++) steps[i].ForeColor = i <= step ? Foreground : Muted;
            details.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\r\n");
        }

        private static void Open(string path)
        {
            if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
