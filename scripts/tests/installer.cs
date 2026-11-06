using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WandEnhancer.Fork.Installer;
using WandEnhancer.Models;

internal static class InstallerTests
{
    private static int passed;
    private static string output;
    private static string corePath;
    private static readonly string[] Order = { "validate", "backup", "remove", "install", "patch", "verify", "launch" };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--watch-updates")
        {
            File.WriteAllText(Path.Combine(args[1], "watcher.pid"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            System.Threading.Thread.Sleep(30000); return 0;
        }
        if (args.Length > 0 && args[0] == "--watch-parent")
        {
            var core = System.Reflection.Assembly.LoadFrom(args[1]);
            core.GetType("WandEnhancer.Core.Services.WatcherAutostart", true).GetMethod("StartDetached", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { args[2], args[3] });
            Console.WriteLine("started"); return 0;
        }
        if (args.Length > 0 && args[0] == "--uninstall")
        {
            File.WriteAllText(Environment.GetEnvironmentVariable("WAND_REMOVAL_TEST_MARKER"), "confirmation-required");
            return 0;
        }
        if (args.Length > 0 && args[0] == "--echo")
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            Console.WriteLine(InstallEngine.Json.Serialize(args.Skip(1).ToArray())); return 0;
        }
        output = args[0];
        corePath = args[1];
        Directory.CreateDirectory(output);
        try
        {
            Test("install performs validation, backup, removal, installation, patch, verification, launch in order", () => {
                var fake = new FakeSteps();
                var reports = new List<int>();
                InstallWorkflow.Run(fake, true, "12.68.0", (step, text) => reports.Add(step));
                Equal(string.Join(",", fake.Calls), string.Join(",", Order));
                Equal(reports.Last(), 6);
            });
            foreach (string operation in Order)
            {
                string failure = operation;
                Test("failure in " + failure + " stops all subsequent actions", () => {
                    var fake = new FakeSteps { FailAt = failure };
                    bool complete = false;
                    Throws(() => InstallWorkflow.Run(fake, true, "12.68.0", (step, text) => complete |= step == 6));
                    Equal(fake.Calls.Count, Array.IndexOf(Order, failure) + 1);
                    Check(!complete, "Failure must never report completion");
                });
            }
            Test("false verification prevents launch", () => {
                var fake = new FakeSteps { Verified = false };
                Throws(() => InstallWorkflow.Run(fake, true, "12.68.0", (step, text) => {}));
                Check(!fake.Calls.Contains("launch"), "Unverified patch launched");
            });
            Test("launch opt-out still verifies the patch", () => {
                var fake = new FakeSteps();
                InstallWorkflow.Run(fake, false, "12.68.0", (step, text) => {});
                Equal(string.Join(",", fake.Calls), string.Join(",", Order.Take(6)));
            });
            Test("busy state rejects double activation and retry resets failed steps", () => {
                var model = new InstallerViewModel("12.68.0");
                Check(model.Begin(), "Did not start"); Check(!model.Begin(), "Double start accepted");
                model.Report(3, "installing"); model.Fail("disk full");
                Equal(model.Stages[3].State, "失败"); Equal(model.ActionText, "重试安装");
                model.Report(6, "stale success"); Equal(model.State, "Error"); Equal(model.Message, "disk full");
                Check(model.Begin(), "Retry rejected"); Equal(model.Progress, 0); Equal(model.Stages[3].State, "等待");
            });
            Test("success preserves launch opt-out and ignores stale updates", () => {
                var model = new InstallerViewModel("12.68.0"); model.Begin(); model.Succeed(false);
                Equal(model.Stages[5].State, "已跳过"); Equal(model.ActionText, "完成"); Check(!model.Begin(), "Success restarted installation");
                model.Report(0, "late callback"); model.Fail("late error"); Equal(model.State, "Success"); Equal(model.Progress, 6);
            });
            Test("progress inputs are bounded", () => {
                var model = new InstallerViewModel("12.68.0"); model.Begin(); model.Report(-1, ""); Equal(model.Progress, 0);
                model.Report(99, ""); Equal(model.Progress, 6);
            });
            Test("startup cleanup matches only commands inside the exact application directory", () => {
                Check(InstallEngine.CommandIsUnder("\"C:\\My Apps\\Wand\\Wand.exe\" --minimized", @"C:\My Apps\Wand"), "Quoted path rejected");
                Check(!InstallEngine.CommandIsUnder(@"C:\Apps\WandOther\Other.exe", @"C:\Apps\Wand"), "Sibling accepted");
                Check(!InstallEngine.CommandIsUnder(@"C:\Apps\Wand\..\Other.exe", @"C:\Apps\Wand"), "Traversal accepted");
                Check(!InstallEngine.CommandIsUnder("\"", @"C:\Apps\Wand"), "Broken quote accepted");
                Check(!InstallEngine.CommandIsUnder("relative.exe", @"C:\Apps\Wand"), "Relative command accepted");
                Environment.SetEnvironmentVariable("WAND_INSTALLER_TEST_ROOT", @"C:\My Apps\Wand");
                try { Check(InstallEngine.CommandIsUnder("\"%WAND_INSTALLER_TEST_ROOT%\\Wand.exe\"", @"C:\My Apps\Wand"), "Expanded command rejected"); }
                finally { Environment.SetEnvironmentVariable("WAND_INSTALLER_TEST_ROOT", null); }
            });
            Test("registry backup preserves kinds, binary values, strings and subkeys", TestRegistry);
            Test("all enhancer options reach an isolated PatchConfig snapshot", () => {
                var options = new PatchOptionsViewModel { Strategy = EPatchStrategy.Supervised, ActivatePro = false, DevTools = true, RemotePanel = true, AutoApply = true };
                var snapshot = options.Snapshot();
                Equal(snapshot.Strategy, EPatchStrategy.Supervised); Check(snapshot.AutoApplyAfterUpdate, "Auto-apply lost");
                Check(snapshot.PatchTypes.SetEquals(new[] { EPatchType.DevToolsOnF12, EPatchType.RemoteWebPanelPreview }), "Patch choices lost");
                options.ActivatePro = true; Check(!snapshot.PatchTypes.Contains(EPatchType.ActivatePro), "Snapshot changed with UI");
                options.DisableUpdates = true; Check(!options.AutoApply, "Conflicting update options enabled");
                options.AutoApply = true; Check(!options.DisableUpdates, "Auto-apply did not allow updates");
            });
            Test("script selections are deduplicated, enable remote access and survive old installation removal", TestScriptOptions);
            Test("auto-apply watcher does not inherit the CLI output pipe", TestWatcherPipe);
            Test("silent removal never launches the original interactive uninstaller", TestSilentRemoval);
            Test("shortcut cleanup backs up only links targeting the removed installation", TestShortcuts);
            Test("silent removal rejects unsafe targets before any stop or cleanup action", () => {
                bool touched = false;
                Throws(() => SilentRemoval.Remove(Path.GetTempPath(), Path.GetTempPath(), "Other", path => touched = true, () => touched = true, () => touched = true));
                Check(!touched, "Unsafe removal had side effects");
            });
            Test("Windows command argument quoting survives a real subprocess round-trip", () => {
                string[] values = { "", "plain", "中文 路径", "a\tb", "embedded\"quote", @"C:\folder with space\", "\\\\\"", "line\nbreak", "--flag=value" };
                var start = new System.Diagnostics.ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                    WandEnhancer.Fork.Arguments.Join(new[] { "--echo" }.Concat(values))) { UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = new System.Text.UTF8Encoding(false) };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    var read = process.StandardOutput.ReadToEndAsync(); Check(process.WaitForExit(5000), "Echo timed out"); Check(read.Wait(5000), "Echo output not closed");
                    string[] actual = InstallEngine.Json.Deserialize<string[]>(read.Result);
                    Check(values.SequenceEqual(actual), "Quoted arguments were changed by Windows parsing");
                }
            });
            Test("standalone GUI process survives its dispatcher startup", () => {
                using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(typeof(InstallerWindow).Assembly.Location) { UseShellExecute = false }))
                try
                {
                    Check(!process.WaitForExit(3000), "Standalone GUI exited during startup; code " + (process.HasExited ? process.ExitCode.ToString() : "unknown"));
                    process.Refresh(); Check(process.MainWindowHandle != IntPtr.Zero, "Standalone GUI has no window");
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            });
            Test("WPF default, busy, failure, success, minimum size and DPI layouts", TestWindow);
            Console.WriteLine("PASS: " + passed + " installer test groups");
            File.WriteAllText(Path.Combine(output, "result.json"), InstallEngine.Json.Serialize(new { success = true, groups = passed }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            File.WriteAllText(Path.Combine(output, "result.json"), InstallEngine.Json.Serialize(new { success = false, groups = passed, error = error.ToString() }));
            return 1;
        }
    }

    private static void TestRegistry()
    {
        string keyPath = @"Software\GBTGAME\WandInstaller.Tests\" + Guid.NewGuid().ToString("N");
        using (var root = Registry.CurrentUser.CreateSubKey(keyPath))
        try
        {
            root.SetValue("text", "中文", RegistryValueKind.String);
            root.SetValue("count", 42, RegistryValueKind.DWord);
            root.SetValue("binary", new byte[] { 0, 127, 255 }, RegistryValueKind.Binary);
            root.SetValue("expand", "%TEMP%\\fixture", RegistryValueKind.ExpandString);
            root.SetValue("multi", new[] { "one", "two" }, RegistryValueKind.MultiString);
            using (var child = root.CreateSubKey("Nested")) child.SetValue("saved", "yes");
            string snapshot = InstallEngine.Json.Serialize(InstallEngine.Snapshot(root));
            foreach (string expected in new[] { "中文", "DWord", "Binary", "ExpandString", "%TEMP%", "MultiString", "Nested", "saved" }) Check(snapshot.Contains(expected), "Missing snapshot value " + expected);
            using (var run = root.CreateSubKey("Run"))
            {
                run.SetValue("own", "\"C:\\Apps\\Wand\\Wand.exe\" --start");
                run.SetValue("other", "\"C:\\Apps\\WandOther\\Other.exe\"");
                var values = InstallEngine.StartupValues(run, @"C:\Apps\Wand");
                Equal(values.Count, 1); Check(values.ContainsKey("own"), "Own startup missing");
            }
            using (var command = root.CreateSubKey(@"Software\Classes\wemod\shell\open\command")) command.SetValue("", "\"C:\\Apps\\Wand\\Wand.exe\" \"%1\"");
            using (var command = root.CreateSubKey(@"Software\Classes\wand\shell\open\command")) command.SetValue("", "\"C:\\Apps\\Other\\Other.exe\" \"%1\"");
            var protocols = UserShellCleanup.AssociatedProtocolKeys(root, @"C:\Apps\Wand").ToArray();
            Equal(protocols.Length, 1); Equal(protocols[0], @"Software\Classes\wemod");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
    }

    private static void TestWatcherPipe()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wand-watch-pipe-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        string child = Path.Combine(scratch, "WandEnhancerWatcher.exe");
        string pidFile = Path.Combine(scratch, "watcher.pid");
        File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, child);
        File.Copy(typeof(InstallerWindow).Assembly.Location, Path.Combine(scratch, "WandSetup.exe"));
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                WandEnhancer.Fork.Arguments.Join(new[] { "--watch-parent", corePath, child, scratch })) { UseShellExecute = false, RedirectStandardOutput = true };
            using (var parent = System.Diagnostics.Process.Start(start))
            {
                var read = parent.StandardOutput.ReadToEndAsync(); Check(parent.WaitForExit(5000), "Watcher starter did not exit");
                Check(read.Wait(1500), "Long-running watcher kept CLI output open");
                Equal(parent.ExitCode, 0);
            }
            for (int i = 0; i < 50 && !File.Exists(pidFile); i++) System.Threading.Thread.Sleep(100);
            Check(File.Exists(pidFile), "Watcher did not start");
        }
        finally
        {
            if (File.Exists(pidFile)) using (var process = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))))
                if (!process.HasExited && string.Equals(process.MainModule.FileName, child, StringComparison.OrdinalIgnoreCase)) { process.Kill(); process.WaitForExit(5000); }
            Directory.Delete(scratch, true);
        }
    }

    private static void TestScriptOptions()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wand-script-test-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(scratch, "old"); Directory.CreateDirectory(old);
        string script = Path.Combine(old, "中文 script.js"); File.WriteAllText(script, "globalThis.fixture = true;");
        try
        {
            var options = new PatchOptionsViewModel(); options.AddScript(script); options.AddScript(script);
            Equal(options.Scripts.Count, 1); Check(options.RemotePanel, "Script did not enable remote access");
            options.Strategy = EPatchStrategy.Supervised; options.AutoApply = true;
            var cached = PatchScriptStore.Copy(options.Snapshot(), Path.Combine(scratch, "cache"));
            File.Delete(script); Directory.Delete(old);
            var saved = PatchScriptStore.Copy(cached, Path.Combine(scratch, "installed", "WandEnhancerScripts"));
            Equal(saved.Strategy, EPatchStrategy.Supervised); Check(saved.AutoApplyAfterUpdate, "Persisted config lost auto-apply");
            Equal(File.ReadAllText(saved.CustomScriptPaths.Single()), "globalThis.fixture = true;");
            Equal(Path.GetFileName(saved.CustomScriptPaths.Single()), "中文 script.js");
            Directory.Delete(Path.Combine(scratch, "cache"), true);
            Check(File.Exists(saved.CustomScriptPaths.Single()), "Auto-apply script depends on temporary cache");
            string configFile = Path.Combine(scratch, "selected.json");
            File.WriteAllText(configFile, InstallEngine.Json.Serialize(saved));
            var start = new System.Diagnostics.ProcessStartInfo(typeof(InstallerWindow).Assembly.Location,
                WandEnhancer.Fork.Arguments.Join(new[] { "--cli", "patch", "--path", Path.Combine(scratch, "missing-install"), "--config", configFile })) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = new System.Text.UTF8Encoding(false)
                };
            using (var process = System.Diagnostics.Process.Start(start))
            {
                var read = process.StandardOutput.ReadToEndAsync(); Check(process.WaitForExit(15000), "Config round-trip timed out"); Check(read.Wait(5000), "Config output not closed");
                Equal(process.ExitCode, 3);
                var reply = InstallEngine.Json.Deserialize<Dictionary<string, object>>(read.Result);
                Equal(Convert.ToInt32(reply["code"]), 3);
            }
            options.Scripts.Clear(); Check(!options.HasScripts, "Script empty state is stale");
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static void TestSilentRemoval()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wand-removal-test-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(scratch, "Local");
        string roaming = Path.Combine(scratch, "Roaming");
        string root = Path.Combine(local, "Wand");
        string marker = Path.Combine(scratch, "confirmation.marker");
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(roaming, "Wand"));
        File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, Path.Combine(root, "Update.exe"));
        File.Copy(typeof(InstallerWindow).Assembly.Location, Path.Combine(root, "WandSetup.exe"));
        File.WriteAllText(Path.Combine(roaming, "Wand", "old.txt"), "old settings");
        string readOnly = Path.Combine(root, "readonly.txt"); File.WriteAllText(readOnly, "read-only application file"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string sibling = Path.Combine(local, "WandOther"); Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "unrelated");
        Environment.SetEnvironmentVariable("WAND_REMOVAL_TEST_MARKER", marker);
        try
        {
            bool stopped = false, registry = false, shortcuts = false;
            SilentRemoval.Remove(local, roaming, "Wand", path => { Equal(path, root); stopped = true; }, () => shortcuts = true, () => registry = true);
            Check(!File.Exists(marker), "Original uninstaller was launched and requested confirmation");
            Check(stopped && registry && shortcuts, "Cleanup steps were skipped");
            Check(!Directory.Exists(root) && !Directory.Exists(Path.Combine(roaming, "Wand")), "Old installation remains");
            Check(File.Exists(Path.Combine(sibling, "keep.txt")), "Unrelated directory was removed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAND_REMOVAL_TEST_MARKER", null);
            Check(Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Invalid fixture cleanup path");
            Directory.Delete(scratch, true);
        }
    }

    private static void TestShortcuts()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wand-shortcut-test-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(scratch, "Wand");
        string programs = Path.Combine(scratch, "Programs");
        string group = Path.Combine(programs, "Wand");
        string desktop = Path.Combine(scratch, "Desktop");
        string backup = Path.Combine(scratch, "Backup");
        Directory.CreateDirectory(root); Directory.CreateDirectory(group); Directory.CreateDirectory(desktop);
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        Action<string, string> create = (path, target) => {
            object link = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            try
            {
                link.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { target });
                link.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
        };
        try
        {
            create(Path.Combine(desktop, "My Wand.lnk"), Path.Combine(root, "Wand.exe"));
            create(Path.Combine(group, "Wand.lnk"), Path.Combine(root, "Update.exe"));
            create(Path.Combine(desktop, "Wand.lnk"), Path.Combine(scratch, "Other", "Other.exe"));
            UserShellCleanup.RemoveShortcuts(root, backup, new Dictionary<string, string> { { "Desktop", desktop }, { "Programs", programs } });
            Check(!File.Exists(Path.Combine(desktop, "My Wand.lnk")), "Owned desktop shortcut remains");
            Check(!Directory.Exists(group), "Empty Wand start menu folder remains");
            Check(File.Exists(Path.Combine(desktop, "Wand.lnk")), "Same-name unrelated shortcut was deleted");
            Check(File.Exists(Path.Combine(backup, "Desktop", "My Wand.lnk")), "Desktop shortcut backup missing");
            Check(File.Exists(Path.Combine(backup, "Programs", "Wand", "Wand.lnk")), "Start menu shortcut backup missing");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            Check(Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Invalid shortcut fixture cleanup path");
            Directory.Delete(scratch, true);
        }
    }

    private static void TestWindow()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new InstallerWindow();
        window.Show();
        try
        {
            var model = (InstallerViewModel)window.DataContext;
            Equal(((TextBlock)window.FindName("AuthorText")).Text, "作者  A-FEI-");
            Equal(((Run)window.FindName("ForumText")).Text, "bbs.gbtgame.me");
            Equal(((Hyperlink)window.FindName("ForumLink")).NavigateUri.AbsoluteUri, "https://bbs.gbtgame.me/");
            Check(((ScrollViewer)window.FindName("SettingsScroll")).VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "Scroll behavior differs from enhancer");
            var originalPalette = new ResourceDictionary { Source = new Uri("/WandSetup;component/Style/ColorScheme.xaml", UriKind.Relative) };
            Equal(((SolidColorBrush)window.FindResource("Background")).Color, ((SolidColorBrush)originalPalette["Background"]).Color);
            Capture(window, "ready", 960, 720, 96);
            model.Begin(); model.Report(3, "正在安装 Wand 12.68.0…"); window.UpdateLayout();
            Check(!((Button)window.FindName("InstallButton")).IsEnabled, "Busy install button enabled");
            Check(!((CheckBox)window.FindName("Launch")).IsEnabled, "Busy launch checkbox enabled");
            window.Close(); Check(window.IsVisible, "Busy window closed");
            Capture(window, "busy", 960, 720, 96);
            model.Fail("测试错误：无法写入目标目录。请检查磁盘空间和文件权限，查看日志后重试。\n" + new string('路', 400));
            Check(((Button)window.FindName("InstallButton")).IsEnabled, "Retry unavailable");
            ((Expander)window.FindName("LogExpander")).IsExpanded = true;
            Capture(window, "error-minimum", 880, 680, 96);
            model.Begin(); model.Succeed(true);
            ((Expander)window.FindName("LogExpander")).IsExpanded = false;
            Capture(window, "success", 960, 720, 96);
            Capture(window, "dpi-150", 960, 720, 144);
            Capture(window, "dpi-200", 960, 720, 192);
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void Capture(InstallerWindow window, string name, double width, double height, double dpi)
    {
        window.Width = width; window.Height = height; window.UpdateLayout();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => {}));
        window.UpdateLayout();
        var message = (TextBlock)window.FindName("MessageText");
        Check(((FrameworkElement)window.FindName("StatusPanel")).ActualHeight < 145, "Status block is too large: " + name);
        Check(message.ActualHeight >= 15, "Status message lost its layout space: " + name);
        foreach (string controlName in new[] { "InstallButton", "Launch", "AuthorText" })
        {
            var control = (FrameworkElement)window.FindName(controlName);
            var point = control.TranslatePoint(new Point(), window);
            Check(control.ActualWidth > 0 && control.ActualHeight > 0 && point.X >= 0 && point.Y >= 0 && point.X + control.ActualWidth <= window.ActualWidth && point.Y + control.ActualHeight <= window.ActualHeight, "Control clipped: " + name + "/" + controlName);
        }
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi / 96), (int)Math.Ceiling(visual.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
    }

    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T actual, T expected) { Check(Equals(actual, expected), "Expected " + expected + ", got " + actual); }
    private static void Throws(Action action) { bool threw = false; try { action(); } catch (InvalidOperationException) { threw = true; } Check(threw, "Expected operation failure"); }

    private sealed class FakeSteps : IInstallSteps
    {
        public List<string> Calls = new List<string>();
        public string FailAt;
        public bool Verified = true;
        private void Call(string name) { Calls.Add(name); if (name == FailAt) throw new InvalidOperationException("Injected " + name + " failure"); }
        public void Validate() { Call("validate"); }
        public void Backup() { Call("backup"); }
        public void Remove() { Call("remove"); }
        public void Install() { Call("install"); }
        public void Patch() { Call("patch"); }
        public bool Verify() { Call("verify"); return Verified; }
        public void Launch() { Call("launch"); }
    }
}
