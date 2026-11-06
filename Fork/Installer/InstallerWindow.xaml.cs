using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Linq;
using WandEnhancer.Branding;
using System.Windows.Controls;

namespace WandEnhancer.Fork.Installer
{
    internal partial class InstallerWindow : Window
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        private readonly InstallerViewModel model;
        private readonly IInstallerInteraction interaction;
        private string logPath;
        private string backupPath;
        private readonly IBackupStore backups;
        private readonly AccordionMotion accordion;
        private bool backupBusy;
        private string pendingBackupAction, pendingBackupPath;
        private IList<BackupEntry> backupEntries = new List<BackupEntry>();
        private int backupPage, logPage;
        private readonly StringBuilder logText = new StringBuilder();
        private List<string> logPages = new List<string> { "" };

        public InstallerWindow() : this(new NativeInstallerInteraction()) { }

        internal InstallerWindow(IInstallerInteraction interaction, IBackupStore backups = null)
        {
            this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
            InitializeComponent();
            this.backups = backups ?? new BackupStore();
            accordion = new AccordionMotion(ContentSections, OptionsExpander, LogExpander, BackupsExpander);
            BackupRootText.Text = this.backups.Root;
            BackupRootText.ToolTip = this.backups.Root;
            model = new InstallerViewModel(InstallEngine.LoadPackage().version);
            model.Configure(interaction.InspectInstallation());
            DataContext = model;
            Title = "GBTGAME · " + model.ShellName;
            RenderLogPage();
            AuthorText.Text = "作者  " + BrandingInfo.Author;
            ForumText.Text = new Uri(BrandingInfo.ForumUrl).Host.ToLowerInvariant();
            ForumLink.NavigateUri = new Uri(BrandingInfo.ForumUrl.ToLowerInvariant());
            model.PropertyChanged += ModelChanged;
            Closing += OnClosing;
            BackupsExpander.Collapsed += (sender, args) => { if (!BackupsExpander.IsExpanded && pendingBackupAction != null && !backupBusy) CancelBackupClick(sender, args); };
            PreviewKeyDown += (sender, args) => { if (args.Key == Key.Escape && pendingBackupAction != null && !backupBusy) { CancelBackupClick(sender, args); args.Handled = true; } };
            Loaded += (sender, args) => { StrategyBox.Focus(); ReloadBackups(); ModelChanged(model, new PropertyChangedEventArgs(nameof(InstallerViewModel.State))); };
            SourceInitialized += (sender, args) =>
            {
                int dark = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
            };
        }

        private void ModelChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(InstallerViewModel.State)) return;
            UpdateControls();
            Title = "GBTGAME · " + model.ShellName;
            StatusTitle.Text = model.State == "Success" ? (model.UseExisting ? "启动完成" : "安装完成") : model.State == "Error" ? "需要处理一个问题" : model.IsBusy ? (model.UseExisting ? "正在准备启动" : "安装进行中") : model.UseExisting ? "已安装，可直接启动" : "准备就绪";
            MessageText.Foreground = (Brush)FindResource(model.State == "Error" ? "Destructive" : "MutedForeground");
        }

        private async void InstallClick(object sender, RoutedEventArgs args)
        {
            if (backupBusy || pendingBackupAction != null) return;
            if (model.State == "Success") { Close(); return; }
            var current = interaction.InspectInstallation();
            if (model.UseExisting && !current.Exists) current = new LauncherSnapshot { Exists = true, Error = "原安装已不存在，已停止启动。请先重新检测，不会自动重装。" };
            model.Configure(current, false);
            if (!model.Begin()) return;
            logText.Clear(); logPages = new List<string> { "" }; logPage = 0; RenderLogPage();
            OpenLog.Visibility = OpenBackup.Visibility = Visibility.Collapsed;
            bool launch = Launch.IsChecked == true;
            IInstallerSession engine = null;
            try
            {
                var config = model.Options.Snapshot();
                engine = interaction.CreateSession((step, text) => Dispatcher.Invoke(() =>
                {
                    model.Report(step, text);
                    AppendLog(text);
                }), config);
                await Task.Run(() => { if (model.UseExisting) engine.StartExisting(launch); else engine.Install(launch); });
                model.Succeed(launch);
            }
            catch (Exception error)
            {
                try { engine?.RecordFailure(error); }
                catch (Exception logError) { AppendLog("无法保存错误日志：" + logError.Message); }
                model.Fail(error.Message);
                AppendLog((model.UseExisting ? "启动已停止：" : "安装已停止：") + error.Message);
                ShowLog();
            }
            finally
            {
                logPath = engine?.LogPath;
                backupPath = engine?.BackupPath;
                try { engine?.Dispose(); }
                catch (Exception logError) { AppendLog("无法关闭日志：" + logError.Message); ShowLog(); }
                OpenLog.Visibility = File.Exists(logPath) ? Visibility.Visible : Visibility.Collapsed;
                OpenBackup.Visibility = Directory.Exists(backupPath) ? Visibility.Visible : Visibility.Collapsed;
                ReloadBackups(backupPath);
            }
        }

        private void AppendLog(string text)
        {
            bool follow = logPage == logPages.Count - 1;
            logText.Append(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            logPages = PaginateLog(logText.ToString());
            if (follow) logPage = logPages.Count - 1;
            RenderLogPage();
        }

        internal static List<string> PaginateLog(string text)
        {
            var pages = new List<string>(); var page = new StringBuilder(); int rows = 0;
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                if (n == lines.Length - 1 && lines[n].Length == 0) break;
                string line = lines[n]; int[] elements = StringInfo.ParseCombiningCharacters(line);
                int chunks = Math.Max(1, (elements.Length + 33) / 34);
                for (int c = 0; c < chunks; c++)
                {
                    int start = elements.Length == 0 ? 0 : elements[c * 34];
                    int end = (c + 1) * 34 < elements.Length ? elements[(c + 1) * 34] : line.Length;
                    page.Append(line.Substring(start, end - start));
                    if (c == chunks - 1 && n < lines.Length - 1) page.Append(Environment.NewLine);
                    if (++rows == 8) { pages.Add(page.ToString()); page.Clear(); rows = 0; }
                }
            }
            if (page.Length > 0 || pages.Count == 0) pages.Add(page.ToString());
            return pages;
        }
        private void RenderLogPage()
        {
            logPage = Math.Max(0, Math.Min(logPage, logPages.Count - 1));
            Details.Text = logPages[logPage];
            LogPageLabel.Text = (logPage + 1) + " / " + logPages.Count;
            PreviousLog.IsEnabled = logPage > 0; NextLog.IsEnabled = logPage < logPages.Count - 1;
        }
        private void PreviousLogClick(object sender, RoutedEventArgs args) { logPage--; RenderLogPage(); }
        private void NextLogClick(object sender, RoutedEventArgs args) { logPage++; RenderLogPage(); }
        private void PreviousScriptsClick(object sender, RoutedEventArgs args) { if (OptionsEditor.IsEnabled) model.Options.MoveScriptPage(-1); }
        private void NextScriptsClick(object sender, RoutedEventArgs args) { if (OptionsEditor.IsEnabled) model.Options.MoveScriptPage(1); }

        private void ShowLog()
        {
            LogExpander.IsExpanded = true;
            if (LogExpander.IsKeyboardFocusWithin || OptionsEditor.IsKeyboardFocusWithin) LogExpander.Focus();
        }

        private void UpdateControls()
        {
            bool busy = model.IsBusy || backupBusy;
            InstallButton.IsEnabled = !busy && pendingBackupAction == null && model.CanRun;
            RefreshInstallation.IsEnabled = !busy && pendingBackupAction == null;
            OptionsEditor.IsEnabled = !busy && model.State != "Success";
            Launch.IsEnabled = !busy && model.State != "Success";
            BackupList.IsEnabled = !busy && pendingBackupAction == null;
            RefreshBackups.IsEnabled = !busy && pendingBackupAction == null;
            PreviousBackups.IsEnabled = !busy && pendingBackupAction == null && backupPage > 0;
            NextBackups.IsEnabled = !busy && pendingBackupAction == null && backupPage + 1 < backupEntries.Count;
            OpenBackupRoot.IsEnabled = !busy;
            var selected = BackupList.SelectedItem as BackupEntry;
            RestoreBackup.IsEnabled = !busy && pendingBackupAction == null && selected?.CanRestore == true;
            DeleteBackup.IsEnabled = OpenBackupFolder.IsEnabled = !busy && pendingBackupAction == null && selected != null;
            ConfirmBackup.IsEnabled = CancelBackup.IsEnabled = !busy;
        }

        private void ReloadBackups(string selectedPath = null)
        {
            try
            {
                selectedPath = selectedPath ?? (BackupList.SelectedItem as BackupEntry)?.Path;
                backupEntries = backups.List();
                int selected = backupEntries.ToList().FindIndex(entry => string.Equals(entry.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
                backupPage = selected >= 0 ? selected : 0;
                BackupsExpander.Header = "配置备份 · " + backupEntries.Count + " 份";
                RenderBackupPage();
            }
            catch (Exception error) { backupEntries = new List<BackupEntry>(); RenderBackupPage(); BackupFeedback.Text = "无法读取备份：" + error.Message; }
            UpdateControls();
        }

        private void RenderBackupPage()
        {
            backupPage = Math.Max(0, Math.Min(backupPage, backupEntries.Count - 1));
            BackupList.ItemsSource = backupEntries.Skip(backupPage).Take(1).ToArray();
            BackupList.SelectedIndex = BackupList.Items.Count > 0 ? 0 : -1;
            BackupPageLabel.Text = (backupPage + 1) + " / " + Math.Max(1, backupEntries.Count);
            EmptyBackups.Visibility = backupEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateControls();
        }
        private void PreviousBackupsClick(object sender, RoutedEventArgs args) { if (PreviousBackups.IsEnabled) { backupPage--; RenderBackupPage(); } }
        private void NextBackupsClick(object sender, RoutedEventArgs args) { if (NextBackups.IsEnabled) { backupPage++; RenderBackupPage(); } }

        private void BackupSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            SelectedBackupPath.Text = (BackupList.SelectedItem as BackupEntry)?.Path ?? "未选择备份";
            SelectedBackupPath.ToolTip = SelectedBackupPath.Text;
            if (model != null) UpdateControls();
        }
        private void RefreshBackupsClick(object sender, RoutedEventArgs args) { if (!backupBusy && !model.IsBusy) ReloadBackups(); }
        private void OpenBackupRootClick(object sender, RoutedEventArgs args)
        {
            try { BackupStore.Guard(backups.Root, false); Directory.CreateDirectory(backups.Root); Open(backups.Root); }
            catch (Exception error) { BackupFeedback.Text = "无法打开备份目录：" + error.Message; }
        }
        private void OpenBackupFolderClick(object sender, RoutedEventArgs args) => Open((BackupList.SelectedItem as BackupEntry)?.Path);
        private void RestoreBackupClick(object sender, RoutedEventArgs args) => RequestBackupAction("restore");
        private void DeleteBackupClick(object sender, RoutedEventArgs args) => RequestBackupAction("delete");
        private void RequestBackupAction(string action)
        {
            var selected = BackupList.SelectedItem as BackupEntry;
            if (backupBusy || model.IsBusy || selected == null || (action == "restore" && !selected.CanRestore)) return;
            pendingBackupAction = action; pendingBackupPath = selected.Path;
            BackupConfirmationText.Text = selected.Label + "\n" + selected.Path + "\n" + (action == "restore" ? "将关闭 Wand / WeMod 并替换用户配置。当前配置会先自动备份，恢复后请手动启动。" : "只删除所选备份，不影响当前 Wand 配置。删除后无法撤销。");
            ConfirmBackup.Content = action == "restore" ? "确认恢复" : "确认删除";
            BackupConfirmation.Visibility = Visibility.Visible; UpdateControls(); ConfirmBackup.Focus();
        }
        private void CancelBackupClick(object sender, RoutedEventArgs args)
        {
            if (backupBusy) return;
            var origin = pendingBackupAction == "restore" ? RestoreBackup : DeleteBackup;
            pendingBackupAction = pendingBackupPath = null; BackupConfirmation.Visibility = Visibility.Collapsed; UpdateControls(); origin.Focus();
        }
        private async void ConfirmBackupClick(object sender, RoutedEventArgs args)
        {
            if (backupBusy || model.IsBusy || pendingBackupAction == null) return;
            string action = pendingBackupAction, path = pendingBackupPath;
            backupBusy = true; UpdateControls();
            BackupFeedback.Text = action == "restore" ? "正在备份当前配置并恢复…" : "正在删除所选备份…";
            string recovery = null;
            try
            {
                await Task.Run(() => { if (action == "restore") recovery = backups.Restore(path); else backups.Delete(path); });
                BackupFeedback.Text = action == "restore" ? "配置已恢复。恢复前备份：" + recovery : "已删除所选备份。";
                BackupFeedback.ToolTip = BackupFeedback.Text; AppendLog(BackupFeedback.Text);
            }
            catch (Exception error) { BackupFeedback.Text = "操作未完成：" + error.Message; AppendLog(BackupFeedback.Text); }
            finally
            {
                backupBusy = false; pendingBackupAction = pendingBackupPath = null;
                BackupConfirmation.Visibility = Visibility.Collapsed;
                ReloadBackups(recovery); UpdateControls();
            }
        }

        private void OnClosing(object sender, CancelEventArgs args)
        {
            if (!model.IsBusy && !backupBusy) return;
            args.Cancel = true;
            AppendLog(backupBusy ? "正在处理备份，请等待当前操作结束后关闭窗口。" : "正在安装，请等待当前流程结束后关闭窗口。");
        }

        private void RefreshInstallationClick(object sender, RoutedEventArgs args)
        {
            if (model.IsBusy || backupBusy || pendingBackupAction != null) return;
            model.Configure(interaction.InspectInstallation(), false);
            Title = "GBTGAME · " + model.ShellName;
        }

        private void OpenLogClick(object sender, RoutedEventArgs args) => Open(logPath);
        private void OpenBackupClick(object sender, RoutedEventArgs args) { BackupsExpander.IsExpanded = true; ReloadBackups(backupPath); }
        private void ForumNavigate(object sender, RequestNavigateEventArgs args) { Open(ForumLink.NavigateUri.AbsoluteUri); args.Handled = true; }

        private void AddScriptClick(object sender, RoutedEventArgs args)
        {
            if (!OptionsEditor.IsEnabled) return;
            try { foreach (string path in interaction.SelectScripts(this)) model.Options.AddScript(path); }
            catch (Exception error) { AppendLog("无法添加脚本：" + error.Message); ShowLog(); }
        }

        private void RemoveScriptClick(object sender, RoutedEventArgs args)
        {
            if (!OptionsEditor.IsEnabled) return;
            var script = (sender as Button)?.Tag as SelectedScript;
            if (script != null) model.Options.Scripts.Remove(script);
        }

        private void Open(string path)
        {
            try { if (!string.IsNullOrEmpty(path)) interaction.Open(path); }
            catch (Exception error) { AppendLog("无法打开：" + error.Message); ShowLog(); }
        }
    }
}
