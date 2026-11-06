using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using WandEnhancer.Branding;
using Microsoft.Win32;
using System.Windows.Controls;

namespace WandEnhancer.Fork.Installer
{
    internal partial class InstallerWindow : Window
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        private readonly InstallerViewModel model;
        private string logPath;
        private string backupPath;

        public InstallerWindow()
        {
            InitializeComponent();
            model = new InstallerViewModel(InstallEngine.LoadPackage().version);
            DataContext = model;
            AuthorText.Text = "作者  " + BrandingInfo.Author;
            ForumText.Text = new Uri(BrandingInfo.ForumUrl).Host.ToLowerInvariant();
            ForumLink.NavigateUri = new Uri(BrandingInfo.ForumUrl.ToLowerInvariant());
            model.PropertyChanged += ModelChanged;
            Closing += OnClosing;
            SourceInitialized += (sender, args) =>
            {
                int dark = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
            };
        }

        private void ModelChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(InstallerViewModel.State)) return;
            InstallButton.IsEnabled = !model.IsBusy;
            OptionsEditor.IsEnabled = !model.IsBusy && model.State != "Success";
            Launch.IsEnabled = !model.IsBusy && model.State != "Success";
            StatusTitle.Text = model.State == "Success" ? "安装完成" : model.State == "Error" ? "需要处理一个问题" : model.IsBusy ? "安装进行中" : "准备就绪";
            MessageText.Foreground = (Brush)FindResource(model.State == "Error" ? "Destructive" : "MutedForeground");
        }

        private async void InstallClick(object sender, RoutedEventArgs args)
        {
            if (model.State == "Success") { Close(); return; }
            if (!model.Begin()) return;
            Details.Clear();
            OpenLog.Visibility = OpenBackup.Visibility = Visibility.Collapsed;
            bool launch = Launch.IsChecked == true;
            InstallEngine engine = null;
            try
            {
                var config = model.Options.Snapshot();
                engine = new InstallEngine((step, text) => Dispatcher.Invoke(() =>
                {
                    model.Report(step, text);
                    AppendLog(text);
                }), config);
                await Task.Run(() => engine.Install(launch));
                model.Succeed(launch);
            }
            catch (Exception error)
            {
                engine?.RecordFailure(error);
                model.Fail(error.Message);
                AppendLog("安装已停止：" + error.Message);
                LogExpander.IsExpanded = true;
            }
            finally
            {
                logPath = engine?.LogPath;
                backupPath = engine?.BackupPath;
                engine?.Dispose();
                OpenLog.Visibility = File.Exists(logPath) ? Visibility.Visible : Visibility.Collapsed;
                OpenBackup.Visibility = Directory.Exists(backupPath) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AppendLog(string text)
        {
            Details.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            Details.ScrollToEnd();
        }

        private void OnClosing(object sender, CancelEventArgs args)
        {
            if (!model.IsBusy) return;
            args.Cancel = true;
            AppendLog("正在安装，请等待当前流程结束后关闭窗口。");
        }

        private void OpenLogClick(object sender, RoutedEventArgs args) => Open(logPath);
        private void OpenBackupClick(object sender, RoutedEventArgs args) => Open(backupPath);
        private void ForumNavigate(object sender, RequestNavigateEventArgs args) { Open(ForumLink.NavigateUri.AbsoluteUri); args.Handled = true; }

        private void AddScriptClick(object sender, RoutedEventArgs args)
        {
            var dialog = new OpenFileDialog { Filter = "JavaScript 文件 (*.js)|*.js", Multiselect = true, CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            try { foreach (string path in dialog.FileNames) model.Options.AddScript(path); }
            catch (Exception error) { AppendLog(error.Message); LogExpander.IsExpanded = true; }
        }

        private void RemoveScriptClick(object sender, RoutedEventArgs args)
        {
            var script = (sender as Button)?.Tag as SelectedScript;
            if (script != null) model.Options.Scripts.Remove(script);
        }

        private void Open(string path)
        {
            try { if (!string.IsNullOrEmpty(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
            catch (Exception error) { AppendLog("无法打开：" + error.Message); LogExpander.IsExpanded = true; }
        }
    }
}
